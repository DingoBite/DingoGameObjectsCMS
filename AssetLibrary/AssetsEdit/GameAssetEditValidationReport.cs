#if NEWTONSOFT_EXISTS
using System;
using System.Collections.Generic;

namespace DingoGameObjectsCMS.AssetLibrary.AssetsEdit
{
    public sealed class GameAssetEditValidationReport
    {
        public bool IsValid { get; }
        public string Summary { get; }
        public IReadOnlyList<string> Messages { get; }

        public GameAssetEditValidationReport(string summary, IReadOnlyList<string> messages)
            : this(messages == null || messages.Count == 0, summary, messages)
        {
        }

        private GameAssetEditValidationReport(bool isValid, string summary, IReadOnlyList<string> messages)
        {
            IsValid = isValid;
            Summary = string.IsNullOrWhiteSpace(summary) ? "Validation" : summary;
            Messages = messages ?? Array.Empty<string>();
        }

        public static GameAssetEditValidationReport Valid(string summary = "Valid")
        {
            return new GameAssetEditValidationReport(true, summary, Array.Empty<string>());
        }

        public static GameAssetEditValidationReport Invalid(string summary, params string[] messages)
        {
            return new GameAssetEditValidationReport(false, summary, messages ?? Array.Empty<string>());
        }
    }
}
#endif
