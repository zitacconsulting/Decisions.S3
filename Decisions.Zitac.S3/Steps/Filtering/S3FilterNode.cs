using System.ComponentModel;
using System.Runtime.Serialization;
using DecisionsFramework;
using DecisionsFramework.Design.ConfigurationStorage.Attributes;
using DecisionsFramework.Design.Properties;
using DecisionsFramework.Design.Properties.Attributes;

namespace Decisions.Zitac.S3.Steps.Filtering
{
    public static class S3FilterNodeType
    {
        public const string Filter = "Filter";
        public const string And = "And";
        public const string Or = "Or";
    }

    public static class S3FilterField
    {
        public const string FileName = "File Name";
        public const string Key = "Key (Full Path)";
        public const string Extension = "Extension";
        public const string Size = "Size (Bytes)";
        public const string LastModified = "Last Modified";
        public const string StorageClass = "Storage Class";
        public const string ContentType = "Content Type";
        public const string Metadata = "Metadata";
        public const string Tag = "Tag";

        public static readonly string[] All = { FileName, Key, Extension, Size, LastModified, StorageClass, ContentType, Metadata, Tag };

        /// <summary>Not in the S3 file listing: needs one HeadObject request per file.</summary>
        public static bool NeedsHead(string fieldName) => fieldName == ContentType || fieldName == Metadata;

        /// <summary>Not in the S3 file listing: needs one GetObjectTagging request per file.</summary>
        public static bool NeedsTags(string fieldName) => fieldName == Tag;

        public static bool HasKeyName(string fieldName) => fieldName == Metadata || fieldName == Tag;
    }

    public static class S3FilterOperator
    {
        // Text
        public const string Equal = "Equals";
        public const string NotEqual = "Not Equals";
        public const string Contains = "Contains";
        public const string DoesNotContain = "Does Not Contain";
        public const string StartsWith = "Starts With";
        public const string EndsWith = "Ends With";
        public const string MatchesWildcard = "Matches Wildcard (* ?)";
        public const string InList = "In List";
        public const string NotInList = "Not In List";
        public const string Exists = "Exists";
        public const string DoesNotExist = "Does Not Exist";
        // Size
        public const string GreaterThan = "Greater Than";
        public const string GreaterOrEqual = "Greater Or Equal";
        public const string LessThan = "Less Than";
        public const string LessOrEqual = "Less Or Equal";
        // Last Modified
        public const string Before = "Before";
        public const string After = "After";
        public const string OlderThanDays = "Older Than (Days)";
        public const string NewerThanDays = "Newer Than (Days)";

        public static readonly string[] Text = { Equal, NotEqual, Contains, DoesNotContain, StartsWith, EndsWith, MatchesWildcard, InList, NotInList };
        public static readonly string[] KeyValue = Text.Concat(new[] { Exists, DoesNotExist }).ToArray();
        public static readonly string[] Number = { Equal, NotEqual, GreaterThan, GreaterOrEqual, LessThan, LessOrEqual };
        public static readonly string[] Date = { Before, After, OlderThanDays, NewerThanDays };

        public static string[] For(string fieldName)
        {
            switch (fieldName)
            {
                case S3FilterField.Size: return Number;
                case S3FilterField.LastModified: return Date;
                case S3FilterField.Metadata:
                case S3FilterField.Tag: return KeyValue;
                default: return Text;
            }
        }

        /// <summary>Operators that are true when the value is missing (e.g. a metadata key the file doesn't have).</summary>
        public static bool IsNegative(string op) => op == NotEqual || op == DoesNotContain || op == NotInList || op == DoesNotExist;
    }

    public enum S3FilterValueKind
    {
        None,
        Text,
        TextList,
        Number,
        Date,
        Days
    }

    public static class S3FilterValueSource
    {
        public const string Value = "Value";
        public const string StepInput = "Step Input";
        public static readonly string[] All = { Value, StepInput };
    }

    /// <summary>
    /// One node of the Search Files filter tree (same idea as Decisions.FetchEntitiesAdvanced's FilterNode):
    /// a condition (Type = Filter) or an And/Or group of child nodes. Values are fixed in the designer or come
    /// from a step input (the step then shows an input with that name).
    /// </summary>
    /// Computed helper properties (dropdown lists, Show* flags, Summary) are [IgnoreDataMember]: Decisions clones the
    /// node with DataContractSerializer, which would otherwise also write get-only arrays and fail reading them back
    /// when their length differs (e.g. 11 operators for Metadata vs 9 for a fresh node) - seen when deleting a filter.
    [Writable]
    public class S3FilterNode : INotifyPropertyChanged
    {
        private const string CATEGORY = "Details";

        [WritableValue] private string nodeType = S3FilterNodeType.Filter;
        [WritableValue] private string fieldName = S3FilterField.FileName;
        [WritableValue] private string keyName;
        [WritableValue] private string operatorValue = S3FilterOperator.Equal;
        [WritableValue] private string valueSource = S3FilterValueSource.Value;
        [WritableValue] private string inputName;
        [WritableValue] private string textValue;
        [WritableValue] private string[] textListValue;
        [WritableValue] private long numberValue;
        [WritableValue] private DateTime? dateValue;
        [WritableValue] private int daysValue;
        [WritableValue] private bool caseSensitive;
        [WritableValue] private S3FilterNode[] children;

        public event PropertyChangedEventHandler PropertyChanged;

        private void Notify(params string[] names)
        {
            foreach (string n in names)
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
        }

        private void NotifyShape()
        {
            Notify(nameof(IsFilterNode), nameof(IsGroupNode), nameof(ShowKeyName), nameof(AvailableOperators), nameof(Operator),
                nameof(ShowValueSource), nameof(ShowInputName), nameof(ShowTextValue), nameof(ShowTextListValue),
                nameof(ShowNumberValue), nameof(ShowDateValue), nameof(ShowDaysValue), nameof(ShowCaseSensitive),
                nameof(ValueKind), nameof(Summary));
        }

        // ---------- Type ----------

        [IgnoreDataMember][PropertyHidden] public string[] AvailableNodeTypes => new[] { S3FilterNodeType.Filter, S3FilterNodeType.And, S3FilterNodeType.Or };

        [PropertyClassification(0, "Type", CATEGORY)]
        [SelectStringEditor(nameof(AvailableNodeTypes), SelectStringEditorType.DropdownList, false)]
        public string NodeType
        {
            get => nodeType;
            set { nodeType = string.IsNullOrEmpty(value) ? S3FilterNodeType.Filter : value; NotifyShape(); Notify(nameof(NodeType)); }
        }

        [IgnoreDataMember][PropertyHidden] public bool IsFilterNode => nodeType == S3FilterNodeType.Filter;
        [IgnoreDataMember][PropertyHidden] public bool IsGroupNode => !IsFilterNode;

        // ---------- Condition ----------

        [IgnoreDataMember][PropertyHidden] public string[] AvailableFields => S3FilterField.All;

        [PropertyClassification(1, "Field", CATEGORY)]
        [SelectStringEditor(nameof(AvailableFields), SelectStringEditorType.DropdownList, false)]
        [BooleanPropertyHidden(nameof(IsFilterNode), false)]
        public string Field
        {
            get => fieldName;
            set
            {
                fieldName = string.IsNullOrEmpty(value) ? S3FilterField.FileName : value;
                if (!S3FilterOperator.For(fieldName).Contains(operatorValue))
                    operatorValue = S3FilterOperator.For(fieldName)[0];
                NotifyShape();
                Notify(nameof(Field));
            }
        }

        [IgnoreDataMember][PropertyHidden] public bool ShowKeyName => IsFilterNode && S3FilterField.HasKeyName(fieldName);

        [PropertyClassification(2, "Key Name", CATEGORY)]
        [BooleanPropertyHidden(nameof(ShowKeyName), false)]
        public string KeyName
        {
            get => keyName;
            set { keyName = value; Notify(nameof(KeyName), nameof(Summary)); }
        }

        [IgnoreDataMember][PropertyHidden] public string[] AvailableOperators => S3FilterOperator.For(fieldName);

        [PropertyClassification(3, "Operator", CATEGORY)]
        [SelectStringEditor(nameof(AvailableOperators), SelectStringEditorType.DropdownList, false)]
        [BooleanPropertyHidden(nameof(IsFilterNode), false)]
        public string Operator
        {
            get => operatorValue;
            set { operatorValue = string.IsNullOrEmpty(value) ? AvailableOperators[0] : value; NotifyShape(); Notify(nameof(Operator)); }
        }

        /// <summary>What kind of value the condition compares against.</summary>
        [IgnoreDataMember][PropertyHidden]
        public S3FilterValueKind ValueKind
        {
            get
            {
                if (!IsFilterNode || operatorValue == S3FilterOperator.Exists || operatorValue == S3FilterOperator.DoesNotExist)
                    return S3FilterValueKind.None;
                if (fieldName == S3FilterField.Size)
                    return S3FilterValueKind.Number;
                if (fieldName == S3FilterField.LastModified)
                    return operatorValue == S3FilterOperator.OlderThanDays || operatorValue == S3FilterOperator.NewerThanDays
                        ? S3FilterValueKind.Days
                        : S3FilterValueKind.Date;
                return operatorValue == S3FilterOperator.InList || operatorValue == S3FilterOperator.NotInList
                    ? S3FilterValueKind.TextList
                    : S3FilterValueKind.Text;
            }
        }

        [IgnoreDataMember][PropertyHidden] public string[] AvailableValueSources => S3FilterValueSource.All;

        [IgnoreDataMember][PropertyHidden] public bool ShowValueSource => ValueKind != S3FilterValueKind.None;

        [PropertyClassification(4, "Value From", CATEGORY)]
        [SelectStringEditor(nameof(AvailableValueSources), SelectStringEditorType.DropdownList, false)]
        [BooleanPropertyHidden(nameof(ShowValueSource), false)]
        public string ValueSource
        {
            get => valueSource;
            set { valueSource = string.IsNullOrEmpty(value) ? S3FilterValueSource.Value : value; NotifyShape(); Notify(nameof(ValueSource)); }
        }

        [IgnoreDataMember][PropertyHidden] public bool IsStepInput => ShowValueSource && valueSource == S3FilterValueSource.StepInput;
        [IgnoreDataMember][PropertyHidden] public bool ShowInputName => IsStepInput;
        private bool ShowConstant(S3FilterValueKind kind) => ShowValueSource && !IsStepInput && ValueKind == kind;

        [PropertyClassification(5, "Input Name", CATEGORY)]
        [BooleanPropertyHidden(nameof(ShowInputName), false)]
        public string InputName
        {
            get => inputName;
            set { inputName = value; Notify(nameof(InputName), nameof(Summary)); }
        }

        [IgnoreDataMember][PropertyHidden] public bool ShowTextValue => ShowConstant(S3FilterValueKind.Text);

        [PropertyClassification(6, "Value", CATEGORY)]
        [BooleanPropertyHidden(nameof(ShowTextValue), false)]
        public string TextValue
        {
            get => textValue;
            set { textValue = value; Notify(nameof(TextValue), nameof(Summary)); }
        }

        [IgnoreDataMember][PropertyHidden] public bool ShowTextListValue => ShowConstant(S3FilterValueKind.TextList);

        [PropertyClassification(7, "Values", CATEGORY)]
        [BooleanPropertyHidden(nameof(ShowTextListValue), false)]
        public string[] TextListValue
        {
            get => textListValue;
            set { textListValue = value; Notify(nameof(TextListValue), nameof(Summary)); }
        }

        [IgnoreDataMember][PropertyHidden] public bool ShowNumberValue => ShowConstant(S3FilterValueKind.Number);

        [PropertyClassification(8, "Size (Bytes)", CATEGORY)]
        [BooleanPropertyHidden(nameof(ShowNumberValue), false)]
        public long NumberValue
        {
            get => numberValue;
            set { numberValue = value; Notify(nameof(NumberValue), nameof(Summary)); }
        }

        [IgnoreDataMember][PropertyHidden] public bool ShowDateValue => ShowConstant(S3FilterValueKind.Date);

        [PropertyClassification(9, "Date", CATEGORY)]
        [BooleanPropertyHidden(nameof(ShowDateValue), false)]
        public DateTime? DateValue
        {
            get => dateValue;
            set { dateValue = value; Notify(nameof(DateValue), nameof(Summary)); }
        }

        [IgnoreDataMember][PropertyHidden] public bool ShowDaysValue => ShowConstant(S3FilterValueKind.Days);

        [PropertyClassification(10, "Days", CATEGORY)]
        [BooleanPropertyHidden(nameof(ShowDaysValue), false)]
        public int DaysValue
        {
            get => daysValue;
            set { daysValue = value; Notify(nameof(DaysValue), nameof(Summary)); }
        }

        [IgnoreDataMember][PropertyHidden]
        public bool ShowCaseSensitive => ValueKind == S3FilterValueKind.Text || ValueKind == S3FilterValueKind.TextList;

        [PropertyClassification(11, "Case Sensitive", CATEGORY)]
        [BooleanPropertyHidden(nameof(ShowCaseSensitive), false)]
        public bool CaseSensitive
        {
            get => caseSensitive;
            set { caseSensitive = value; Notify(nameof(CaseSensitive)); }
        }

        // ---------- Group ----------

        [PropertyClassification(12, "Conditions", CATEGORY)]
        [BooleanPropertyHidden(nameof(IsGroupNode), false)]
        public S3FilterNode[] Children
        {
            get => children;
            set { children = value; Notify(nameof(Children), nameof(Summary)); }
        }

        // ---------- Display / validation ----------

        [IgnoreDataMember][PropertyHidden]
        public string Summary => ToString();

        public override string ToString()
        {
            if (IsGroupNode)
                return $"{nodeType.ToUpperInvariant()} ({children?.Length ?? 0} condition{((children?.Length ?? 0) == 1 ? "" : "s")})";

            string subject = S3FilterField.HasKeyName(fieldName) ? $"{fieldName} '{keyName}'" : fieldName;
            string value;
            if (IsStepInput)
                value = $"[{inputName}]";
            else
            {
                switch (ValueKind)
                {
                    case S3FilterValueKind.None: value = ""; break;
                    case S3FilterValueKind.TextList: value = "(" + string.Join(", ", textListValue ?? Array.Empty<string>()) + ")"; break;
                    case S3FilterValueKind.Number: value = numberValue.ToString(); break;
                    case S3FilterValueKind.Date: value = dateValue?.ToString("yyyy-MM-dd HH:mm") ?? "?"; break;
                    case S3FilterValueKind.Days: value = daysValue.ToString(); break;
                    default: value = $"'{textValue}'"; break;
                }
            }
            return $"{subject} {operatorValue.ToLowerInvariant()} {value}".Trim();
        }

        /// <summary>Validation messages for this node and its children ("path" = position, for readable messages).</summary>
        public IEnumerable<string> GetProblems(string path)
        {
            if (IsGroupNode)
            {
                if (children == null || children.Length == 0)
                    yield return $"{path}: the {nodeType.ToUpperInvariant()} group has no conditions";
                for (int i = 0; i < (children?.Length ?? 0); i++)
                    if (children[i] != null)
                        foreach (string p in children[i].GetProblems($"{path}.{i + 1}"))
                            yield return p;
                yield break;
            }

            if (S3FilterField.HasKeyName(fieldName) && string.IsNullOrWhiteSpace(keyName))
                yield return $"{path}: {fieldName} filter needs a Key Name";
            if (IsStepInput)
            {
                if (string.IsNullOrWhiteSpace(inputName))
                    yield return $"{path}: Input Name is required when the value comes from a step input";
                yield break;
            }
            switch (ValueKind)
            {
                case S3FilterValueKind.Text when textValue == null:
                    yield return $"{path}: Value is required"; break;
                case S3FilterValueKind.TextList when textListValue == null || textListValue.Length == 0:
                    yield return $"{path}: Values needs at least one entry"; break;
                case S3FilterValueKind.Date when dateValue == null:
                    yield return $"{path}: Date is required"; break;
                case S3FilterValueKind.Days when daysValue < 0:
                    yield return $"{path}: Days can't be negative"; break;
                case S3FilterValueKind.Number when numberValue < 0:
                    yield return $"{path}: Size can't be negative"; break;
            }
        }

        /// <summary>The step input this node adds (name, type, is list), or null.</summary>
        public (string Name, Type Type, bool IsList)? StepInputDefinition()
        {
            if (!IsFilterNode || !IsStepInput || string.IsNullOrWhiteSpace(inputName))
                return null;
            switch (ValueKind)
            {
                case S3FilterValueKind.TextList: return (inputName.Trim(), typeof(string), true);
                case S3FilterValueKind.Number: return (inputName.Trim(), typeof(long), false);
                case S3FilterValueKind.Date: return (inputName.Trim(), typeof(DateTime), false);
                case S3FilterValueKind.Days: return (inputName.Trim(), typeof(int), false);
                default: return (inputName.Trim(), typeof(string), false);
            }
        }
    }
}
