using System.Text;
using System.Text.RegularExpressions;

namespace Decisions.Zitac.S3.Connection
{
    public enum MetadataEncoding
    {
        /// <summary>
        /// Values with non-ASCII (or control) characters become RFC 2047 encoded-words (=?UTF-8?B?...?=);
        /// plain ASCII values are stored unchanged. The standard for non-ASCII in headers, and what AWS documents for S3.
        /// </summary>
        Rfc2047,
        /// <summary>Every value is percent-encoded (Uri.EscapeDataString), so decoding is unambiguous.</summary>
        UrlEncode,
        /// <summary>Only printable ASCII values are allowed.</summary>
        Reject
    }

    /// <summary>
    /// Encodes/decodes S3 user-metadata values, which travel as HTTP headers and are only reliably ASCII.
    /// Done here instead of with AWSConfigsS3.EnableUnicodeEncodingForObjectMetadata, which is process-wide
    /// and would also change Decisions.AWS / Decisions.Zitac.AWS behavior on the same server.
    /// </summary>
    public static class MetadataCodec
    {
        private const string PREFIX = "=?UTF-8?B?";
        private const string SUFFIX = "?=";
        // RFC 2047: an encoded-word is at most 75 characters. 75 - 12 (prefix + suffix) = 63 Base64 chars
        // = at most 45 bytes per word (Base64 works in 3-byte groups).
        private const int MAX_BYTES_PER_WORD = 45;

        // charset (optionally *language per RFC 2231), encoding B or Q, encoded text.
        private static readonly Regex EncodedWord = new Regex(
            @"=\?(?<charset>[^?*\s]+)(\*[^?\s]*)?\?(?<enc>[BbQq])\?(?<text>[^?\s]*)\?=",
            RegexOptions.CultureInvariant);

        // Whitespace between two adjacent encoded-words is not part of the text (RFC 2047 section 6.2).
        private static readonly Regex WhitespaceBetweenWords = new Regex(@"(?<=\?=)\s+(?==\?)", RegexOptions.CultureInvariant);

        public static string Encode(string value, MetadataEncoding encoding, string keyForErrors)
        {
            value ??= string.Empty;
            switch (encoding)
            {
                case MetadataEncoding.UrlEncode:
                    return Uri.EscapeDataString(value);

                case MetadataEncoding.Reject:
                    if (!IsPlainAscii(value))
                        throw new ArgumentException($"Metadata value for '{keyForErrors}' contains non-ASCII or control characters, which the Metadata Encoding setting (Reject) does not allow");
                    return value;

                default:
                    // Also encode ASCII values that look like encoded-words, so they read back exactly as written.
                    return IsPlainAscii(value) && !EncodedWord.IsMatch(value) ? value : EncodeRfc2047(value);
            }
        }

        public static string Decode(string value, MetadataEncoding encoding)
        {
            if (string.IsNullOrEmpty(value))
                return value ?? string.Empty;

            if (encoding == MetadataEncoding.UrlEncode)
            {
                try { return Uri.UnescapeDataString(value); }
                catch (UriFormatException) { return value; }
            }

            // RFC 2047 is decoded in every mode: it's unambiguous, and servers (AWS among them) may return it.
            return value.Contains("=?", StringComparison.Ordinal) ? DecodeRfc2047(value) : value;
        }

        public static bool IsPlainAscii(string value)
        {
            foreach (char c in value)
                if (c < 0x20 || c > 0x7E)
                    return false;
            return true;
        }

        /// <summary>Splits into encoded-words of at most 45 UTF-8 bytes, never inside a character, separated by spaces.</summary>
        public static string EncodeRfc2047(string value)
        {
            var words = new List<string>();
            var chunk = new StringBuilder();
            int chunkBytes = 0;

            for (int i = 0; i < value.Length; i++)
            {
                // Keep surrogate pairs (characters outside the BMP, e.g. emoji) together.
                int length = char.IsHighSurrogate(value[i]) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]) ? 2 : 1;
                string ch = value.Substring(i, length);
                int bytes = Encoding.UTF8.GetByteCount(ch);

                if (chunkBytes + bytes > MAX_BYTES_PER_WORD && chunk.Length > 0)
                {
                    words.Add(ToWord(chunk.ToString()));
                    chunk.Clear();
                    chunkBytes = 0;
                }
                chunk.Append(ch);
                chunkBytes += bytes;
                i += length - 1;
            }
            if (chunk.Length > 0 || words.Count == 0)
                words.Add(ToWord(chunk.ToString()));

            return string.Join(" ", words);
        }

        public static string DecodeRfc2047(string value)
        {
            string joined = WhitespaceBetweenWords.Replace(value, string.Empty);
            return EncodedWord.Replace(joined, m =>
            {
                try
                {
                    Encoding charset = Encoding.GetEncoding(m.Groups["charset"].Value);
                    string text = m.Groups["text"].Value;
                    byte[] bytes = char.ToUpperInvariant(m.Groups["enc"].Value[0]) == 'B'
                        ? Convert.FromBase64String(text)
                        : DecodeQ(text);
                    return charset.GetString(bytes);
                }
                catch (Exception ex) when (ex is ArgumentException || ex is FormatException || ex is NotSupportedException)
                {
                    // Unknown charset or malformed word: leave it as it is rather than lose the value.
                    return m.Value;
                }
            });
        }

        private static string ToWord(string text) => PREFIX + Convert.ToBase64String(Encoding.UTF8.GetBytes(text)) + SUFFIX;

        private static byte[] DecodeQ(string text)
        {
            var bytes = new List<byte>(text.Length);
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c == '_')
                    bytes.Add((byte)' ');
                else if (c == '=' && i + 2 < text.Length)
                {
                    bytes.Add(Convert.ToByte(text.Substring(i + 1, 2), 16));
                    i += 2;
                }
                else
                    bytes.Add((byte)c);
            }
            return bytes.ToArray();
        }
    }
}
