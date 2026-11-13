using System.ComponentModel;
using Amazon.S3;
using Amazon.S3.Model;
using Decisions.Zitac.S3.Connection;
using Decisions.Zitac.S3.Steps.Filtering;
using DecisionsFramework;
using DecisionsFramework.Design.ConfigurationStorage.Attributes;
using DecisionsFramework.Design.Flow;
using DecisionsFramework.Design.Flow.Mapping;
using DecisionsFramework.Design.Flow.Mapping.InputImpl;
using DecisionsFramework.Design.Properties;
using DecisionsFramework.Design.Properties.Attributes;

namespace Decisions.Zitac.S3.Steps
{
    /// <summary>
    /// Lists files under a prefix (the only filter S3 applies itself) and filters them with an AND/OR filter tree
    /// (Filters property, same idea as Decisions.FetchEntitiesAdvanced).
    ///
    /// Each file is first evaluated with only the listing data (name, key, extension, size, date, storage class).
    /// Conditions on metadata/content type/tags are Unknown at that point; only files whose result is still Unknown
    /// get the extra request(s) (HeadObject, then GetObjectTagging if still needed), Parallel Requests at a time and at
    /// most Max Files To Inspect per call. Results and paging are applied in key order, so they are the same as
    /// with one-by-one checking.
    ///
    /// Paging uses StartAfter/Marker = the last processed key, which both ListObjects V1 and V2 support.
    /// </summary>
    [AutoRegisterStep("Search Files", STEP_CATEGORY, STEP_SUBCATEGORY, "Zitac")]
    [Writable]
    // Panel order: Decisions puts Inputs at 100 and Outputs at 101 (RootOutputMappingGenerator, 9.32 + 10.4), so the search
    // sections are sub-sections of Inputs (Inputs > Where To Search etc.) to stay above Outputs. Rarely needed ones
    // (Performance, Connection Settings) are collapsed above Inputs.
    [CategoryClassification(1, INPUTS_CATEGORY, WHERE_CATEGORY)]
    [CategoryClassification(2, INPUTS_CATEGORY, FILTERS_CATEGORY)]
    [CategoryClassification(3, INPUTS_CATEGORY, RESULTS_CATEGORY)]
    [CategoryClassification(60, PERFORMANCE_CATEGORY)]
    [CategoryInDialog(new[] { PERFORMANCE_CATEGORY }, Collapsed = true, InDialog = false)]
    public class SearchFiles : BaseS3Step, IDefaultInputMappingStep
    {
        private const string WHERE_CATEGORY = "Where To Search";
        private const string FILTERS_CATEGORY = "Filters";
        private const string RESULTS_CATEGORY = "Results And Paging";
        private const string PERFORMANCE_CATEGORY = "Performance";
        private const string INPUT_PREFIX = "Prefix";
        private const string INPUT_MAX_RESULTS = "Max Results";
        private const string INPUT_CONTINUE_FROM = "Continue From";

        private const string OUTPUT_FILES = "Files";
        private const string OUTPUT_FOLDERS = "Folders";
        private const string OUTPUT_HAS_MORE = "Has More";
        private const string OUTPUT_CONTINUE_FROM = "Continue From Next";

        private const int DEFAULT_MAX_RESULTS = 100;
        private const int S3_PAGE_SIZE = 1000;
        private const int MAX_LIST_CALLS = 100;
        private const char FOLDER_DELIMITER = '/';
        // Appended to a folder prefix to make StartAfter skip everything inside it (keys sort by UTF-8 bytes).
        private const string AFTER_FOLDER_SUFFIX = "￿";
        private const int DEFAULT_PARALLEL_REQUESTS = 10;
        private const int MAX_PARALLEL_REQUESTS = 50;

        private static readonly HashSet<string> ReservedInputNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            INPUT_CREDENTIALS, INPUT_CONNECTION_SETTINGS, INPUT_BUCKET, INPUT_PREFIX, INPUT_MAX_RESULTS, INPUT_CONTINUE_FROM
        };

        [WritableValue] private S3FilterNode[] filters;
        [WritableValue] private bool includeSubfolders = true;
        [WritableValue] private bool includeMetadata;
        [WritableValue] private bool includeTags;
        [WritableValue] private int maxFilesToInspect = 1000;
        [WritableValue] private int parallelRequests = DEFAULT_PARALLEL_REQUESTS;

        private bool filterEventsWired;

        protected override string FeatureName =>
            includeTags || AllNodes(filters).Any(n => n.IsFilterNode && S3FilterField.NeedsTags(n.Field))
                ? "object tagging (used by a Tag filter or Include Tags)"
                : null;

        // ---------- Filters ----------

        [PropertyClassification(new[] { INPUTS_CATEGORY, FILTERS_CATEGORY }, "Filters Info", 0)]
        [InfoOrWarningEditor(false, null, true)]
        public string FiltersNote
        {
            get
            {
                return "All top-level filters must match (AND); use an And/Or group for other combinations. No filters = all files under the Prefix. "
                     + "Key (Full Path) filters are checked by the module after listing; for a fixed start of the path use Prefix instead, which the server applies (much faster). "
                     + "File Name, Key, Extension, Size, Last Modified and Storage Class are free to check. Metadata and Content Type cost one extra request per file, Tag another one; "
                     + "those requests are only made for files the free conditions can't decide. A filter whose step input is empty at runtime is skipped.";
            }
            set { }
        }

        [PropertyClassification(new[] { INPUTS_CATEGORY, FILTERS_CATEGORY }, "Filters", 1)]
        public S3FilterNode[] Filters
        {
            get { EnsureFilterEventsWired(); return filters; }
            set
            {
                WireFilterEvents(filters, attach: false);
                filters = value;
                WireFilterEvents(filters, attach: true);
                filterEventsWired = true;
                OnPropertyChanged(nameof(Filters));
                OnPropertyChanged(nameof(InputData));
            }
        }

        // ---------- Where to search ----------

        [PropertyClassification(new[] { INPUTS_CATEGORY, WHERE_CATEGORY }, "Prefix Info", 0)]
        [InfoOrWarningEditor(false, null, true)]
        public string PrefixNote
        {
            get
            {
                return "Prefix = the start of the file's full path in the bucket, e.g. invoices/2026/ (that folder and everything below it) or invoices/2026/inv-12. "
                     + "It is the only filter the S3 server applies itself, so the narrower it is, the faster the search. Empty = the whole bucket.";
            }
            set { }
        }

        [PropertyClassification(1, "Include Subfolders", INPUTS_CATEGORY, WHERE_CATEGORY)]
        public bool IncludeSubfolders
        {
            get { return includeSubfolders; }
            set { includeSubfolders = value; OnPropertyChanged(nameof(IncludeSubfolders)); }
        }

        // ---------- Results and paging ----------

        [PropertyClassification(0, "Include Metadata In Results", INPUTS_CATEGORY, RESULTS_CATEGORY)]
        public bool IncludeMetadata
        {
            get { return includeMetadata; }
            set { includeMetadata = value; OnPropertyChanged(nameof(IncludeMetadata)); }
        }

        [PropertyClassification(1, "Include Tags In Results", INPUTS_CATEGORY, RESULTS_CATEGORY)]
        public bool IncludeTags
        {
            get { return includeTags; }
            set { includeTags = value; OnPropertyChanged(nameof(IncludeTags)); }
        }

        [PropertyClassification(new[] { INPUTS_CATEGORY, RESULTS_CATEGORY }, "Results Info", 2)]
        [InfoOrWarningEditor(false, null, true)]
        public string ResultsNote
        {
            get
            {
                return "Results contain what the S3 file list has (key, name, size, last modified, ETag, storage class) plus whatever the filters fetched. "
                     + "Include Metadata In Results (with content type) and Include Tags In Results cost one extra request per returned file each. "
                     + "At most Max Results files are returned per call: if Has More is true, call the step again with Continue From = Continue From Next for the next batch (leave Continue From empty the first time).";
            }
            set { }
        }

        // ---------- Performance ----------

        [PropertyClassification(new[] { PERFORMANCE_CATEGORY }, "Performance Info", 0)]
        [InfoOrWarningEditor(false, null, true)]
        public string PerformanceNote
        {
            get
            {
                return "Metadata, content type and tag conditions need one extra request per file the other conditions can't decide. They run Parallel Requests at a time, at most Max Files To Inspect per call (Has More then continues). "
                     + "For frequent searches over many files, put the search terms in the path (e.g. invoices/2026/customer-123/) or keep an index in Decisions.";
            }
            set { }
        }

        [PropertyClassification(1, "Max Files To Inspect", PERFORMANCE_CATEGORY)]
        public int MaxFilesToInspect
        {
            get { return maxFilesToInspect; }
            set { maxFilesToInspect = value; OnPropertyChanged(nameof(MaxFilesToInspect)); }
        }

        [PropertyClassification(2, "Parallel Requests", PERFORMANCE_CATEGORY)]
        public int ParallelRequests
        {
            get { return parallelRequests; }
            set { parallelRequests = value; OnPropertyChanged(nameof(ParallelRequests)); }
        }

        // ---------- Inputs / outputs ----------

        protected override IEnumerable<DataDescription> StepInputs
        {
            get
            {
                var inputs = new List<DataDescription>
                {
                    new DataDescription(typeof(string), INPUT_BUCKET),
                    new DataDescription(typeof(string), INPUT_PREFIX)
                    {
                        Categories = new[] { INPUTS_CATEGORY, WHERE_CATEGORY },
                        Description = "Start of the files' full path, e.g. invoices/2026/. The only filter the S3 server applies itself."
                    },
                    new DataDescription(typeof(int), INPUT_MAX_RESULTS)
                    {
                        Categories = new[] { INPUTS_CATEGORY, RESULTS_CATEGORY },
                        Description = "Maximum number of files (and folders) returned per call."
                    },
                    new DataDescription(typeof(string), INPUT_CONTINUE_FROM)
                    {
                        Categories = new[] { INPUTS_CATEGORY, RESULTS_CATEGORY },
                        Description = "Empty for the first call; Continue From Next of the previous call to get the next batch."
                    }
                };
                var seen = new HashSet<string>(ReservedInputNames, StringComparer.OrdinalIgnoreCase);
                foreach (S3FilterNode node in AllNodes(filters))
                {
                    var def = node.StepInputDefinition();
                    if (def != null && seen.Add(def.Value.Name))
                        inputs.Add(new DataDescription(def.Value.Type, def.Value.Name, def.Value.IsList));
                }
                return inputs;
            }
        }

        public IInputMapping[] DefaultInputs => new IInputMapping[]
        {
            new IgnoreInputMapping { InputDataName = INPUT_PREFIX },
            new ConstantInputMapping { InputDataName = INPUT_MAX_RESULTS, Value = DEFAULT_MAX_RESULTS },
            new IgnoreInputMapping { InputDataName = INPUT_CONTINUE_FROM }
        };

        protected override DataDescription[] DoneOutputs => new[]
        {
            new DataDescription(typeof(S3FileInfo), OUTPUT_FILES, true),
            new DataDescription(typeof(string), OUTPUT_FOLDERS, true),
            new DataDescription(typeof(bool), OUTPUT_HAS_MORE),
            new DataDescription(typeof(string), OUTPUT_CONTINUE_FROM)
        };

        public override ValidationIssue[] GetValidationIssues()
        {
            var issues = new List<ValidationIssue>(base.GetValidationIssues());
            if (maxFilesToInspect < 1)
                issues.Add(new ValidationIssue(this, "Max Files To Inspect must be at least 1", "", BreakLevel.Fatal, nameof(MaxFilesToInspect)));
            if (parallelRequests < 1 || parallelRequests > MAX_PARALLEL_REQUESTS)
                issues.Add(new ValidationIssue(this, $"Parallel Requests must be between 1 and {MAX_PARALLEL_REQUESTS}", "", BreakLevel.Fatal, nameof(ParallelRequests)));

            for (int i = 0; i < (filters?.Length ?? 0); i++)
                if (filters[i] != null)
                    foreach (string problem in filters[i].GetProblems($"Filter {i + 1}"))
                        issues.Add(new ValidationIssue(this, problem, "", BreakLevel.Fatal, nameof(Filters)));

            var inputTypes = new Dictionary<string, (Type, bool)>(StringComparer.OrdinalIgnoreCase);
            foreach (S3FilterNode node in AllNodes(filters))
            {
                var def = node.StepInputDefinition();
                if (def == null)
                    continue;
                if (ReservedInputNames.Contains(def.Value.Name))
                    issues.Add(new ValidationIssue(this, $"Filter input name '{def.Value.Name}' is already used by the step; choose another", "", BreakLevel.Fatal, nameof(Filters)));
                else if (inputTypes.TryGetValue(def.Value.Name, out var existing) && existing != (def.Value.Type, def.Value.IsList))
                    issues.Add(new ValidationIssue(this, $"Filter input '{def.Value.Name}' is used by filters with different value types; give them different names", "", BreakLevel.Fatal, nameof(Filters)));
                else
                    inputTypes[def.Value.Name] = (def.Value.Type, def.Value.IsList);
            }
            return issues.ToArray();
        }

        // ---------- Run ----------

        protected override Dictionary<string, object> RunStep(StepStartData data, AmazonS3Client client, ResolvedConnection connection)
        {
            string bucket = GetString(data, INPUT_BUCKET);
            S3Helper.RequireBucket(bucket);
            bucket = bucket.Trim();

            string prefix = (GetString(data, INPUT_PREFIX) ?? string.Empty).Replace('\\', '/').TrimStart('/');
            int maxResults = GetValue(data, INPUT_MAX_RESULTS, DEFAULT_MAX_RESULTS);
            if (maxResults < 1)
                maxResults = DEFAULT_MAX_RESULTS;
            string startAfter = GetString(data, INPUT_CONTINUE_FROM);
            if (string.IsNullOrEmpty(startAfter))
                startAfter = null;
            int parallel = Math.Clamp(parallelRequests, 1, MAX_PARALLEL_REQUESTS);

            S3CompiledFilter filter = S3CompiledFilter.Compile(filters, name => data.Data.TryGetValue(name, out object v) ? v : null, DateTime.UtcNow);

            var files = new List<S3FileInfo>();
            var folders = new List<string>();
            int inspectionBudget = Math.Max(1, maxFilesToInspect);
            bool serverHasMore = true;
            bool stoppedEarly = false;

            for (int call = 0; call < MAX_LIST_CALLS && serverHasMore && !stoppedEarly; call++)
            {
                ListPage page = List(client, connection, bucket, prefix, startAfter);
                serverHasMore = page.IsTruncated;

                // Free evaluation with listing data only.
                var verdicts = new Tri[page.Items.Count];
                var contexts = new S3FilterContext[page.Items.Count];
                for (int n = 0; n < page.Items.Count; n++)
                {
                    ListItem item = page.Items[n];
                    if (item.IsFolder || item.Key.EndsWith('/')) // folders, and zero-byte "folder marker" objects
                    {
                        verdicts[n] = Tri.False;
                        continue;
                    }
                    contexts[n] = S3FilterContext.FromListing(item.Object);
                    verdicts[n] = filter.Evaluate(contexts[n]);
                }

                // Walk the page in segments. Each segment ends just before the (batch size + 1)th undecided file; the
                // segment's undecided files are inspected in parallel, then all its items are applied in key order.
                int i = 0;
                while (i < page.Items.Count && !stoppedEarly)
                {
                    int batchSize = Math.Min(parallel, inspectionBudget);
                    var candidates = new List<int>();
                    bool budgetUsedUp = false;
                    int end = i;
                    for (; end < page.Items.Count; end++)
                    {
                        if (verdicts[end] != Tri.Unknown)
                            continue;
                        if (candidates.Count >= batchSize)
                        {
                            // Batch full; with no budget left, stop before this file so the next call starts here.
                            budgetUsedUp = batchSize == 0;
                            break;
                        }
                        candidates.Add(end);
                    }

                    InspectAll(client, connection, bucket, filter, contexts, verdicts, candidates);
                    inspectionBudget -= candidates.Count;

                    for (int j = i; j < end; j++)
                    {
                        ListItem item = page.Items[j];
                        // A folder whose contents we skipped via AFTER_FOLDER_SUFFIX can be returned again; ignore it.
                        if (item.IsFolder && startAfter != null && startAfter == item.Key + AFTER_FOLDER_SUFFIX)
                            continue;

                        startAfter = item.IsFolder ? item.Key + AFTER_FOLDER_SUFFIX : item.Key;

                        if (item.IsFolder)
                            folders.Add(item.Key);
                        else if (verdicts[j] == Tri.True)
                            files.Add(ToInfo(bucket, item.Object, contexts[j], connection));

                        if (files.Count + folders.Count >= maxResults)
                        {
                            stoppedEarly = j < page.Items.Count - 1 || serverHasMore;
                            serverHasMore = false;
                            break;
                        }
                    }

                    if (budgetUsedUp)
                        stoppedEarly = true;
                    i = end;
                }

                // A truncated page with no items (some servers do this) still moves forward via its own marker.
                if (page.Items.Count == 0 && serverHasMore && page.NextMarker != null)
                    startAfter = page.NextMarker;
            }

            if (includeMetadata || includeTags)
                Enrich(client, bucket, files, parallel, connection);

            bool hasMore = stoppedEarly || serverHasMore;
            return new Dictionary<string, object>
            {
                { OUTPUT_FILES, files.ToArray() },
                { OUTPUT_FOLDERS, folders.ToArray() },
                { OUTPUT_HAS_MORE, hasMore },
                { OUTPUT_CONTINUE_FROM, hasMore ? startAfter : null }
            };
        }

        /// <summary>
        /// Fetches what the filter still needs for the undecided files, concurrently (the caller limits the batch to
        /// Parallel Requests). HeadObject first; GetObjectTagging only if the result is still undecided.
        /// </summary>
        private static void InspectAll(AmazonS3Client client, ResolvedConnection connection, string bucket, S3CompiledFilter filter,
            S3FilterContext[] contexts, Tri[] verdicts, List<int> candidates)
        {
            if (candidates.Count == 0)
                return;
            Task<Tri>[] tasks = candidates.Select(index => InspectAsync(client, connection, bucket, filter, contexts[index])).ToArray();
            Task.WhenAll(tasks).GetAwaiter().GetResult();
            for (int n = 0; n < candidates.Count; n++)
                verdicts[candidates[n]] = tasks[n].Result;
        }

        private static async Task<Tri> InspectAsync(AmazonS3Client client, ResolvedConnection connection, string bucket, S3CompiledFilter filter, S3FilterContext file)
        {
            try
            {
                Tri result = Tri.Unknown;
                if (filter.UsesHead)
                {
                    await LoadHeadAsync(client, connection, bucket, file).ConfigureAwait(false);
                    result = filter.Evaluate(file);
                }
                if (result == Tri.Unknown && filter.UsesTags)
                {
                    await LoadTagsAsync(client, bucket, file).ConfigureAwait(false);
                    result = filter.Evaluate(file);
                }
                return result == Tri.True ? Tri.True : Tri.False;
            }
            catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound && ex.ErrorCode != "NoSuchBucket")
            {
                // Deleted between listing and inspecting: not a match, not an error.
                return Tri.False;
            }
        }

        private static async Task LoadHeadAsync(AmazonS3Client client, ResolvedConnection connection, string bucket, S3FilterContext file)
        {
            GetObjectMetadataResponse head = await client.GetObjectMetadataAsync(new GetObjectMetadataRequest { BucketName = bucket, Key = file.Key }).ConfigureAwait(false);
            // Values are decoded, so filters compare against the real text (e.g. "Göteborg").
            file.Metadata = S3Helper.ReadMetadata(head.Metadata, connection.MetadataEncoding)
                .GroupBy(m => m.Key, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First().Value, StringComparer.OrdinalIgnoreCase);
            file.ContentType = head.Headers?.ContentType;
            file.HeadLoaded = true;
        }

        private static async Task LoadTagsAsync(AmazonS3Client client, string bucket, S3FilterContext file)
        {
            GetObjectTaggingResponse tags = await client.GetObjectTaggingAsync(new GetObjectTaggingRequest { BucketName = bucket, Key = file.Key }).ConfigureAwait(false);
            file.Tags = S3Helper.FromTags(tags.Tagging)
                .GroupBy(t => t.Key, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.First().Value, StringComparer.Ordinal);
            file.TagsLoaded = true;
        }

        /// <summary>Adds metadata/content type and/or tags to results that don't have them yet (Include ... In Results).</summary>
        private void Enrich(AmazonS3Client client, string bucket, List<S3FileInfo> files, int parallel, ResolvedConnection connection)
        {
            var throttle = new SemaphoreSlim(parallel);
            Task[] tasks = files.Select(async info =>
            {
                await throttle.WaitAsync().ConfigureAwait(false);
                try
                {
                    var file = new S3FilterContext { Key = info.Key };
                    if (includeMetadata && info.Metadata == null)
                    {
                        await LoadHeadAsync(client, connection, bucket, file).ConfigureAwait(false);
                        info.Metadata = ToKeyValues(file.Metadata);
                        info.ContentType = file.ContentType;
                    }
                    if (includeTags && info.Tags == null)
                    {
                        await LoadTagsAsync(client, bucket, file).ConfigureAwait(false);
                        info.Tags = ToKeyValues(file.Tags);
                    }
                }
                catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound && ex.ErrorCode != "NoSuchBucket")
                {
                    // Deleted in the meantime: keep the listing data.
                }
                finally
                {
                    throttle.Release();
                }
            }).ToArray();
            Task.WhenAll(tasks).GetAwaiter().GetResult();
        }

        private ListPage List(AmazonS3Client client, ResolvedConnection connection, string bucket, string prefix, string startAfter)
        {
            string delimiter = includeSubfolders ? null : FOLDER_DELIMITER.ToString();
            var page = new ListPage();
            List<S3Object> objects;
            List<string> prefixes;

            if (connection.ListApiVersion == ListApiVersion.V1)
            {
                var request = new ListObjectsRequest { BucketName = bucket, Prefix = prefix, MaxKeys = S3_PAGE_SIZE };
                if (delimiter != null) request.Delimiter = delimiter;
                if (startAfter != null) request.Marker = startAfter;
                ListObjectsResponse response = client.ListObjectsAsync(request).GetAwaiter().GetResult();
                objects = response.S3Objects;
                prefixes = response.CommonPrefixes;
                page.IsTruncated = response.IsTruncated == true;
                page.NextMarker = response.NextMarker;
            }
            else
            {
                var request = new ListObjectsV2Request { BucketName = bucket, Prefix = prefix, MaxKeys = S3_PAGE_SIZE };
                if (delimiter != null) request.Delimiter = delimiter;
                if (startAfter != null) request.StartAfter = startAfter;
                ListObjectsV2Response response = client.ListObjectsV2Async(request).GetAwaiter().GetResult();
                objects = response.S3Objects;
                prefixes = response.CommonPrefixes;
                page.IsTruncated = response.IsTruncated == true;
            }

            // AWS SDK v4 leaves empty collections null.
            page.Items.AddRange((objects ?? new List<S3Object>()).Select(o => new ListItem { Key = o.Key, Object = o }));
            page.Items.AddRange((prefixes ?? new List<string>()).Select(p => new ListItem { Key = p, IsFolder = true }));
            // S3 orders by UTF-8 bytes; ordinal UTF-16 order is the same except for characters outside the BMP.
            page.Items.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));
            if (page.NextMarker == null && page.IsTruncated && page.Items.Count > 0)
                page.NextMarker = page.Items[page.Items.Count - 1].Key;
            return page;
        }

        private static S3FileInfo ToInfo(string bucket, S3Object o, S3FilterContext file, ResolvedConnection connection)
        {
            var info = new S3FileInfo
            {
                Bucket = bucket,
                Key = o.Key,
                FileName = S3Helper.FileNameFromKey(o.Key),
                Size = o.Size ?? 0,
                LastModified = o.LastModified,
                ETag = o.ETag,
                StorageClass = o.StorageClass?.Value
            };
            // Whatever the filter already fetched is passed on.
            if (file != null && file.HeadLoaded)
            {
                info.Metadata = ToKeyValues(file.Metadata);
                info.ContentType = file.ContentType;
            }
            if (file != null && file.TagsLoaded)
                info.Tags = ToKeyValues(file.Tags);
            return info;
        }

        private static S3KeyValue[] ToKeyValues(Dictionary<string, string> values) =>
            (values ?? new Dictionary<string, string>())
                .Select(kv => new S3KeyValue { Key = kv.Key, Value = kv.Value })
                .OrderBy(kv => kv.Key, StringComparer.Ordinal)
                .ToArray();

        // ---------- Filter tree plumbing ----------

        private static IEnumerable<S3FilterNode> AllNodes(S3FilterNode[] nodes)
        {
            foreach (S3FilterNode node in nodes ?? Array.Empty<S3FilterNode>())
            {
                if (node == null)
                    continue;
                yield return node;
                foreach (S3FilterNode child in AllNodes(node.Children))
                    yield return child;
            }
        }

        /// <summary>A step loaded from a saved flow got its filters without the setter, so subscribe on first use.</summary>
        private void EnsureFilterEventsWired()
        {
            if (filterEventsWired)
                return;
            filterEventsWired = true;
            WireFilterEvents(filters, attach: true);
        }

        private void WireFilterEvents(S3FilterNode[] nodes, bool attach)
        {
            foreach (S3FilterNode node in AllNodes(nodes))
            {
                node.PropertyChanged -= OnFilterChanged;
                if (attach)
                    node.PropertyChanged += OnFilterChanged;
            }
        }

        private void OnFilterChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(S3FilterNode.Children))
                WireFilterEvents(filters, attach: true); // new child nodes need subscribing too
            // Field, operator, value source and input name all change which step inputs exist.
            OnPropertyChanged(nameof(InputData));
        }

        private sealed class ListItem
        {
            public string Key;
            public bool IsFolder;
            public S3Object Object;
        }

        private sealed class ListPage
        {
            public readonly List<ListItem> Items = new List<ListItem>();
            public bool IsTruncated;
            public string NextMarker;
        }
    }
}
