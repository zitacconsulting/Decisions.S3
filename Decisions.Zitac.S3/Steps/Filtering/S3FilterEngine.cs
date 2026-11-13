using System.Text.RegularExpressions;
using Amazon.S3.Model;
using Decisions.Zitac.S3.Connection;

namespace Decisions.Zitac.S3.Steps.Filtering
{
    /// <summary>Three-valued result: a condition on metadata/tags is Unknown until those have been fetched.</summary>
    public enum Tri
    {
        False,
        True,
        Unknown
    }

    /// <summary>What is known about one file while filtering.</summary>
    public sealed class S3FilterContext
    {
        public string Key;
        public long Size;
        public DateTime? LastModified;
        public string StorageClass;

        /// <summary>Filled after HeadObject (null = not fetched yet). Keys without the x-amz-meta- prefix, lowercase.</summary>
        public Dictionary<string, string> Metadata;
        public string ContentType;
        public bool HeadLoaded;

        /// <summary>Filled after GetObjectTagging (null = not fetched yet).</summary>
        public Dictionary<string, string> Tags;
        public bool TagsLoaded;

        public static S3FilterContext FromListing(S3Object o) => new S3FilterContext
        {
            Key = o.Key,
            Size = o.Size ?? 0,
            LastModified = o.LastModified?.ToUniversalTime(),
            StorageClass = o.StorageClass?.Value
        };
    }

    /// <summary>
    /// The designer filter tree with step-input values resolved and patterns compiled, ready to evaluate per file.
    /// Top-level filters are combined with AND. A condition whose step input is null is skipped (counts as true),
    /// like Decisions.FetchEntitiesAdvanced does.
    /// </summary>
    public sealed class S3CompiledFilter
    {
        private readonly Node root;

        public bool UsesHead { get; }
        public bool UsesTags { get; }

        private S3CompiledFilter(Node root)
        {
            this.root = root;
            UsesHead = root.Any(n => n.Field != null && S3FilterField.NeedsHead(n.Field));
            UsesTags = root.Any(n => n.Field != null && S3FilterField.NeedsTags(n.Field));
        }

        public static S3CompiledFilter Compile(S3FilterNode[] nodes, Func<string, object> getInput, DateTime nowUtc)
        {
            var root = new Node { Group = S3FilterNodeType.And, Children = CompileNodes(nodes, getInput, nowUtc) };
            return new S3CompiledFilter(root);
        }

        public Tri Evaluate(S3FilterContext file) => root.Evaluate(file);

        private static List<Node> CompileNodes(S3FilterNode[] nodes, Func<string, object> getInput, DateTime nowUtc)
        {
            var list = new List<Node>();
            foreach (S3FilterNode n in nodes ?? Array.Empty<S3FilterNode>())
            {
                if (n == null)
                    continue;
                if (n.IsGroupNode)
                {
                    list.Add(new Node { Group = n.NodeType, Children = CompileNodes(n.Children, getInput, nowUtc) });
                    continue;
                }

                var node = new Node
                {
                    Field = n.Field,
                    KeyName = n.KeyName?.Trim(),
                    Operator = n.Operator,
                    Comparison = n.CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase
                };

                object value;
                if (n.IsStepInput)
                {
                    value = getInput(n.InputName?.Trim() ?? "");
                    if (value == null)
                    {
                        node.Skip = true; // no value given at runtime: this condition doesn't filter
                        list.Add(node);
                        continue;
                    }
                }
                else
                {
                    switch (n.ValueKind)
                    {
                        case S3FilterValueKind.TextList: value = n.TextListValue; break;
                        case S3FilterValueKind.Number: value = n.NumberValue; break;
                        case S3FilterValueKind.Date: value = n.DateValue; break;
                        case S3FilterValueKind.Days: value = n.DaysValue; break;
                        case S3FilterValueKind.None: value = null; break;
                        default: value = n.TextValue; break;
                    }
                }

                switch (n.ValueKind)
                {
                    case S3FilterValueKind.Text:
                        node.Text = NormalizeText(n.Field, Convert.ToString(value) ?? "");
                        if (n.Operator == S3FilterOperator.MatchesWildcard)
                            node.Pattern = WildcardToRegex(node.Text, n.CaseSensitive);
                        break;
                    case S3FilterValueKind.TextList:
                        node.List = ((value as IEnumerable<string>) ?? Array.Empty<string>())
                            .Where(v => v != null).Select(v => NormalizeText(n.Field, v)).ToArray();
                        break;
                    case S3FilterValueKind.Number:
                        node.Number = Convert.ToInt64(value);
                        break;
                    case S3FilterValueKind.Date:
                        if (value is DateTime dt && dt != default)
                            node.Date = dt.Kind == DateTimeKind.Utc ? dt : dt.ToUniversalTime();
                        else
                            node.Skip = true;
                        break;
                    case S3FilterValueKind.Days:
                        node.Date = nowUtc.AddDays(-Convert.ToInt32(value));
                        break;
                }
                list.Add(node);
            }
            return list;
        }

        /// <summary>Extensions are compared without the dot ("pdf" and ".pdf" both work).</summary>
        private static string NormalizeText(string field, string value) =>
            field == S3FilterField.Extension ? value.Trim().TrimStart('.') : value;

        private static Regex WildcardToRegex(string pattern, bool caseSensitive)
        {
            string regex = "^" + Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$";
            RegexOptions options = RegexOptions.CultureInvariant | RegexOptions.Singleline;
            if (!caseSensitive)
                options |= RegexOptions.IgnoreCase;
            return new Regex(regex, options);
        }

        private sealed class Node
        {
            // Group
            public string Group;
            public List<Node> Children;
            // Condition
            public string Field;
            public string KeyName;
            public string Operator;
            public StringComparison Comparison;
            public bool Skip;
            public string Text;
            public string[] List;
            public Regex Pattern;
            public long Number;
            public DateTime? Date;

            public bool Any(Func<Node, bool> predicate) =>
                Group != null ? Children.Any(c => c.Any(predicate)) : (!Skip && predicate(this));

            public Tri Evaluate(S3FilterContext f)
            {
                if (Group != null)
                {
                    if (Children.Count == 0)
                        return Tri.True;
                    bool unknown = false;
                    foreach (Node child in Children)
                    {
                        Tri r = child.Evaluate(f);
                        if (Group == S3FilterNodeType.Or && r == Tri.True) return Tri.True;
                        if (Group != S3FilterNodeType.Or && r == Tri.False) return Tri.False;
                        if (r == Tri.Unknown) unknown = true;
                    }
                    if (unknown) return Tri.Unknown;
                    return Group == S3FilterNodeType.Or ? Tri.False : Tri.True;
                }

                if (Skip)
                    return Tri.True;
                if (S3FilterField.NeedsHead(Field) && !f.HeadLoaded)
                    return Tri.Unknown;
                if (S3FilterField.NeedsTags(Field) && !f.TagsLoaded)
                    return Tri.Unknown;

                switch (Field)
                {
                    case S3FilterField.Size:
                        return ToTri(CompareNumber(f.Size));
                    case S3FilterField.LastModified:
                        return ToTri(CompareDate(f.LastModified));
                    default:
                        return ToTri(CompareText(TextOf(f)));
                }
            }

            private static Tri ToTri(bool b) => b ? Tri.True : Tri.False;

            private string TextOf(S3FilterContext f)
            {
                switch (Field)
                {
                    case S3FilterField.FileName: return S3Helper.FileNameFromKey(f.Key);
                    case S3FilterField.Key: return f.Key;
                    case S3FilterField.Extension:
                        string name = S3Helper.FileNameFromKey(f.Key);
                        int dot = name.LastIndexOf('.');
                        return dot < 0 ? "" : name.Substring(dot + 1);
                    case S3FilterField.StorageClass: return f.StorageClass ?? "STANDARD";
                    case S3FilterField.ContentType: return f.ContentType;
                    case S3FilterField.Metadata: return Lookup(f.Metadata);
                    case S3FilterField.Tag: return Lookup(f.Tags);
                    default: return null;
                }
            }

            /// <summary>Metadata/tag keys are matched case-insensitively (S3 stores metadata keys lowercase anyway).</summary>
            private string Lookup(Dictionary<string, string> values)
            {
                if (values == null || string.IsNullOrEmpty(KeyName))
                    return null;
                foreach (KeyValuePair<string, string> kv in values)
                    if (string.Equals(kv.Key, KeyName, StringComparison.OrdinalIgnoreCase))
                        return kv.Value ?? "";
                return null;
            }

            /// <summary>A missing value (metadata/tag key not on the file) only satisfies the negative operators.</summary>
            private bool CompareText(string actual)
            {
                if (Operator == S3FilterOperator.Exists) return actual != null;
                if (Operator == S3FilterOperator.DoesNotExist) return actual == null;
                if (actual == null) return S3FilterOperator.IsNegative(Operator);

                switch (Operator)
                {
                    case S3FilterOperator.Equal: return string.Equals(actual, Text, Comparison);
                    case S3FilterOperator.NotEqual: return !string.Equals(actual, Text, Comparison);
                    case S3FilterOperator.Contains: return actual.IndexOf(Text, Comparison) >= 0;
                    case S3FilterOperator.DoesNotContain: return actual.IndexOf(Text, Comparison) < 0;
                    case S3FilterOperator.StartsWith: return actual.StartsWith(Text, Comparison);
                    case S3FilterOperator.EndsWith: return actual.EndsWith(Text, Comparison);
                    case S3FilterOperator.MatchesWildcard: return Pattern.IsMatch(actual);
                    case S3FilterOperator.InList: return List.Any(v => string.Equals(actual, v, Comparison));
                    case S3FilterOperator.NotInList: return !List.Any(v => string.Equals(actual, v, Comparison));
                    default: return false;
                }
            }

            private bool CompareNumber(long actual)
            {
                switch (Operator)
                {
                    case S3FilterOperator.Equal: return actual == Number;
                    case S3FilterOperator.NotEqual: return actual != Number;
                    case S3FilterOperator.GreaterThan: return actual > Number;
                    case S3FilterOperator.GreaterOrEqual: return actual >= Number;
                    case S3FilterOperator.LessThan: return actual < Number;
                    case S3FilterOperator.LessOrEqual: return actual <= Number;
                    default: return false;
                }
            }

            private bool CompareDate(DateTime? actual)
            {
                if (actual == null || Date == null)
                    return false;
                switch (Operator)
                {
                    case S3FilterOperator.Before:
                    case S3FilterOperator.OlderThanDays: return actual < Date;
                    case S3FilterOperator.After:
                    case S3FilterOperator.NewerThanDays: return actual >= Date;
                    default: return false;
                }
            }
        }
    }
}
