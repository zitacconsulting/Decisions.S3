using System.Runtime.Serialization;
using Amazon.S3;
using Decisions.Zitac.S3.Connection;
using DecisionsFramework;
using DecisionsFramework.Design.ConfigurationStorage.Attributes;
using DecisionsFramework.Design.Flow;
using DecisionsFramework.Design.Flow.CoreSteps;
using DecisionsFramework.Design.Flow.Mapping;
using DecisionsFramework.Design.Properties;
using DecisionsFramework.Design.Properties.Attributes;

namespace Decisions.Zitac.S3.Steps
{
    /// <summary>
    /// Connection model (no module settings, so nothing differs between Decisions 9.x and 10.x):
    /// - "Credentials" input (S3Credentials): server URL, region and keys, i.e. what differs between environments.
    /// - Connection settings: how to talk to that server. Step properties with notes by default (the defaults work with
    ///   most servers); with "Provide Settings as Input" they come from a "Connection Settings" input instead,
    ///   e.g. one flow variable shared by many steps.
    /// Errors go to the Error outcome with a message that says which field to check.
    /// </summary>
    [Writable]
    // Connection Settings start collapsed: the defaults are right for most servers (same attribute core steps use for "Advanced").
    [CategoryInDialog(new[] { SETTINGS_CATEGORY }, Collapsed = true, InDialog = false)]
    // Collapsed, between Step Information (-10) and Inputs (100): out of the way but visible as a heading.
    [CategoryClassification(50, SETTINGS_CATEGORY)]
    public abstract class BaseS3Step : BaseFlowAwareStep, ISyncStep, IDataConsumer, IDataProducer, IValidationSource
    {
        protected const string STEP_CATEGORY = "Integration";
        protected const string STEP_SUBCATEGORY = "S3";
        protected const string SETTINGS_CATEGORY = "Connection Settings";
        protected const string INPUTS_CATEGORY = "Inputs";
        protected const string INPUT_CREDENTIALS = "Credentials";
        protected const string INPUT_CONNECTION_SETTINGS = "Connection Settings";
        protected const string INPUT_BUCKET = "Bucket";
        protected const string INPUT_KEY = "Key";
        protected const string OUTCOME_DONE = "Done";
        protected const string OUTCOME_ERROR = "Error";
        protected const string OUTPUT_ERROR_MESSAGE = "Error Message";

        [WritableValue] private bool settingsAsInput;
        [WritableValue] private bool useVirtualHostedStyle;
        [WritableValue] private bool useAwsChecksums;
        [WritableValue] private ListApiVersion listApiVersion = ListApiVersion.V2;
        [WritableValue] private bool acceptUntrustedCertificate;
        [WritableValue] private int timeoutSeconds = ResolvedConnection.DEFAULT_TIMEOUT_SECONDS;
        [WritableValue] private MetadataEncoding metadataEncoding = MetadataEncoding.Rfc2047;

        /// <summary>Name used in "This S3 server does not support ..." messages.</summary>
        protected virtual string FeatureName => null;

        protected const string OUTCOME_NOT_FOUND = "Not Found";
        protected const string OUTCOMES_CATEGORY = "Outcomes";

        [WritableValue] private bool showNotFoundOutcome;

        /// <summary>Steps that work on one existing file offer the optional Not Found outcome.</summary>
        protected virtual bool SupportsNotFoundOption => false;

        [IgnoreDataMember][PropertyHidden]
        public bool ShowNotFoundOption => SupportsNotFoundOption;

        // Same placement and wording style as Decisions.Proxmox's "Show Outcome for No Results".
        [PropertyClassification(0, "Show Outcome For Not Found", OUTCOMES_CATEGORY)]
        [BooleanPropertyHidden(nameof(ShowNotFoundOption), false)]
        public bool ShowNotFoundOutcome
        {
            get { return showNotFoundOutcome; }
            set
            {
                showNotFoundOutcome = value;
                OnPropertyChanged(nameof(ShowNotFoundOutcome));
                OnPropertyChanged(nameof(OutcomeScenarios));
            }
        }

        /// <summary>
        /// When not null, a missing file (FileNotFoundInS3Exception, or S3's NoSuchKey / 404 for the file) goes to this
        /// outcome instead of Error. Get File Info always has it; the other single-file steps when Show Outcome For Not Found is on.
        /// </summary>
        protected virtual string NotFoundOutcome => SupportsNotFoundOption && showNotFoundOutcome ? OUTCOME_NOT_FOUND : null;

        // ---------- Connection settings (step properties) ----------
        // [BooleanPropertyHidden(nameof(X), true)] = hidden while X is true (see DecisionsKB notes/module-settings.md).

        [PropertyClassification(0, "Provide Settings as Input", SETTINGS_CATEGORY)]
        public bool SettingsAsInput
        {
            get { return settingsAsInput; }
            set
            {
                settingsAsInput = value;
                OnPropertyChanged(nameof(SettingsAsInput));
                OnPropertyChanged(nameof(InputData));
            }
        }

        [PropertyClassification(1, "About These Settings", SETTINGS_CATEGORY)]
        [InfoOrWarningEditor(false, null, true)]
        public string SettingsNote
        {
            get
            {
                return settingsAsInput
                    ? "The settings come from the Connection Settings input (S3ConnectionSettings). Fields left empty use the defaults, which work with most S3 servers."
                    : "The defaults work with most S3 servers. Only change these if the step fails, Check S3 Features says so, or the S3 administrator tells you to. Tick Provide Settings as Input to share one setting between many steps.";
            }
            set { }
        }

        [PropertyClassification(2, "Use Virtual Hosted Style", SETTINGS_CATEGORY)]
        [BooleanPropertyHidden(nameof(SettingsAsInput), true)]
        public bool UseVirtualHostedStyle
        {
            get { return useVirtualHostedStyle; }
            set { useVirtualHostedStyle = value; OnPropertyChanged(nameof(UseVirtualHostedStyle)); }
        }

        [PropertyClassification(3, "Virtual Hosted Style Info", SETTINGS_CATEGORY)]
        [InfoOrWarningEditor(false, null, true)]
        [BooleanPropertyHidden(nameof(SettingsAsInput), true)]
        public string VirtualHostedStyleNote
        {
            get { return "Off: files are addressed as https://server/bucket/file, which works everywhere. On: https://bucket.server/file, which needs DNS and a certificate for every bucket name. Turn on only if the S3 administrator says so, or for Amazon S3 buckets that require it."; }
            set { }
        }

        [PropertyClassification(4, "Use AWS Checksums", SETTINGS_CATEGORY)]
        [BooleanPropertyHidden(nameof(SettingsAsInput), true)]
        public bool UseAwsChecksums
        {
            get { return useAwsChecksums; }
            set { useAwsChecksums = value; OnPropertyChanged(nameof(UseAwsChecksums)); }
        }

        [PropertyClassification(5, "AWS Checksums Info", SETTINGS_CATEGORY)]
        [InfoOrWarningEditor(false, null, true)]
        [BooleanPropertyHidden(nameof(SettingsAsInput), true)]
        public string AwsChecksumsNote
        {
            get { return "Off: uploads are sent in the classic way every S3 server understands. On: adds the extra checksums Amazon S3 uses by default, which many other S3 servers reject (uploads fail). Leave off unless you use Amazon S3 and want them."; }
            set { }
        }

        [PropertyClassification(6, "List API Version", SETTINGS_CATEGORY)]
        [BooleanPropertyHidden(nameof(SettingsAsInput), true)]
        public ListApiVersion ListApiVersion
        {
            get { return listApiVersion; }
            set { listApiVersion = value; OnPropertyChanged(nameof(ListApiVersion)); }
        }

        [PropertyClassification(7, "List API Version Info", SETTINGS_CATEGORY)]
        [InfoOrWarningEditor(false, null, true)]
        [BooleanPropertyHidden(nameof(SettingsAsInput), true)]
        public string ListApiVersionNote
        {
            get { return "Used when listing files (Search Files). Keep V2. Use V1 only for old S3 servers where Check S3 Features reports that V2 fails and V1 works."; }
            set { }
        }

        [PropertyClassification(8, "Accept Untrusted Certificate", SETTINGS_CATEGORY)]
        [BooleanPropertyHidden(nameof(SettingsAsInput), true)]
        public bool AcceptUntrustedCertificate
        {
            get { return acceptUntrustedCertificate; }
            set
            {
                acceptUntrustedCertificate = value;
                OnPropertyChanged(nameof(AcceptUntrustedCertificate));
                OnPropertyChanged(nameof(UntrustedCertificateWarning));
            }
        }

        [PropertyClassification(9, "Certificate Warning", SETTINGS_CATEGORY)]
        [InfoOrWarningEditor(false, null, false)]
        [BooleanPropertyHidden(nameof(SettingsAsInput), true)]
        [BooleanPropertyHidden(nameof(AcceptUntrustedCertificate), false)]
        public string UntrustedCertificateWarning
        {
            get { return "Certificate checks are off: the connection is encrypted, but Decisions can't verify it is really talking to your S3 server. Better fix: ask IT to trust the S3 server's certificate (or its internal CA) on the Decisions server, then turn this off."; }
            set { }
        }

        [PropertyClassification(10, "Timeout Seconds", SETTINGS_CATEGORY)]
        [BooleanPropertyHidden(nameof(SettingsAsInput), true)]
        public int TimeoutSeconds
        {
            get { return timeoutSeconds; }
            set { timeoutSeconds = value; OnPropertyChanged(nameof(TimeoutSeconds)); }
        }

        [PropertyClassification(11, "Metadata Encoding", SETTINGS_CATEGORY)]
        [BooleanPropertyHidden(nameof(SettingsAsInput), true)]
        public MetadataEncoding MetadataEncoding
        {
            get { return metadataEncoding; }
            set { metadataEncoding = value; OnPropertyChanged(nameof(MetadataEncoding)); }
        }

        [PropertyClassification(12, "Metadata Encoding Info", SETTINGS_CATEGORY)]
        [InfoOrWarningEditor(false, null, true)]
        [BooleanPropertyHidden(nameof(SettingsAsInput), true)]
        public string MetadataEncodingNote
        {
            get { return "How metadata values with characters like å, ä, ö are stored. Rfc2047 (recommended) is the standard for this; the steps decode it again when reading. Use the same setting in all flows that read or write the same files."; }
            set { }
        }

        // ---------- Inputs / outcomes ----------

        public DataDescription[] InputData
        {
            get
            {
                // Same (default) input category as the step's other inputs.
                var inputs = new List<DataDescription>
                {
                    new DataDescription(typeof(S3Credentials), INPUT_CREDENTIALS)
                };
                if (settingsAsInput)
                    inputs.Add(new DataDescription(typeof(S3ConnectionSettings), INPUT_CONNECTION_SETTINGS));
                inputs.AddRange(StepInputs);
                return inputs.ToArray();
            }
        }

        protected abstract IEnumerable<DataDescription> StepInputs { get; }

        protected abstract DataDescription[] DoneOutputs { get; }

        public override OutcomeScenarioData[] OutcomeScenarios
        {
            get
            {
                var outcomes = new List<OutcomeScenarioData> { new OutcomeScenarioData(OUTCOME_DONE, DoneOutputs) };
                if (NotFoundOutcome != null)
                    outcomes.Add(new OutcomeScenarioData(NotFoundOutcome));
                outcomes.Add(new OutcomeScenarioData(OUTCOME_ERROR, new DataDescription(typeof(string), OUTPUT_ERROR_MESSAGE)));
                return outcomes.ToArray();
            }
        }

        /// <summary>S3's "this file doesn't exist" (not a missing bucket). HEAD requests have no error body, so just "NotFound".</summary>
        protected static bool IsFileNotFound(Exception ex)
        {
            while ((ex is AggregateException || ex is System.Reflection.TargetInvocationException) && ex.InnerException != null)
                ex = ex.InnerException;
            return ex is FileNotFoundInS3Exception
                || (ex is AmazonS3Exception s3 && s3.ErrorCode != "NoSuchBucket"
                    && (s3.ErrorCode == "NoSuchKey" || s3.ErrorCode == "NotFound" || s3.StatusCode == System.Net.HttpStatusCode.NotFound));
        }

        public ResultData Run(StepStartData data)
        {
            ResolvedConnection connection = null;
            try
            {
                S3ConnectionSettings settings = settingsAsInput
                    ? GetValue<S3ConnectionSettings>(data, INPUT_CONNECTION_SETTINGS)
                    : SettingsFromProperties();
                connection = ResolvedConnection.From(GetValue<S3Credentials>(data, INPUT_CREDENTIALS), settings);
                using AmazonS3Client client = S3ClientFactory.Create(connection);
                return new ResultData(OUTCOME_DONE, RunStep(data, client, connection));
            }
            catch (Exception ex) when (NotFoundOutcome != null && IsFileNotFound(ex))
            {
                return new ResultData(NotFoundOutcome);
            }
            catch (Exception ex)
            {
                return new ResultData(OUTCOME_ERROR, new Dictionary<string, object>
                {
                    { OUTPUT_ERROR_MESSAGE, S3Helper.DescribeException(ex, FeatureName, connection) }
                });
            }
        }

        protected abstract Dictionary<string, object> RunStep(StepStartData data, AmazonS3Client client, ResolvedConnection connection);

        private S3ConnectionSettings SettingsFromProperties()
        {
            return new S3ConnectionSettings
            {
                UseVirtualHostedStyle = useVirtualHostedStyle,
                UseAwsChecksums = useAwsChecksums,
                ListApiVersion = listApiVersion,
                AcceptUntrustedCertificate = acceptUntrustedCertificate,
                TimeoutSeconds = timeoutSeconds,
                MetadataEncoding = metadataEncoding
            };
        }

        public virtual ValidationIssue[] GetValidationIssues()
        {
            var issues = new List<ValidationIssue>();
            if (!settingsAsInput && timeoutSeconds < 1)
                issues.Add(new ValidationIssue(this, "Timeout Seconds must be at least 1", "", BreakLevel.Fatal, nameof(TimeoutSeconds)));
            return issues.ToArray();
        }

        protected static string GetString(StepStartData data, string name)
        {
            return data.Data.TryGetValue(name, out object value) ? value as string : null;
        }

        protected static T GetValue<T>(StepStartData data, string name, T defaultValue = default)
        {
            return data.Data.TryGetValue(name, out object value) && value is T t ? t : defaultValue;
        }
    }
}
