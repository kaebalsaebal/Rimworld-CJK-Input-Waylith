using UnityEngine;
using Verse;

namespace FcitxCjkInput {
    public sealed class FcitxCjkInputSettings : ModSettings {
        public bool DebugLog;
        public RightAltKeyMode RightAltMode = RightAltKeyMode.AltRight;
        public BackQuoteKeyMode BackQuoteMode = BackQuoteKeyMode.BackQuote;
        public bool ForceRightAltHangul;
        public bool ForceBackquoteZenkakuHankaku;

        public override void ExposeData() {
            Scribe_Values.Look(ref DebugLog, "debugLog", false);
            Scribe_Values.Look(ref RightAltMode, "rightAltMode", RightAltKeyMode.AltRight);
            Scribe_Values.Look(ref BackQuoteMode, "backQuoteMode", BackQuoteKeyMode.BackQuote);
            Scribe_Values.Look(ref ForceRightAltHangul, "forceRightAltHangul",
                RightAltMode == RightAltKeyMode.Hangul);
            Scribe_Values.Look(ref ForceBackquoteZenkakuHankaku, "forceBackquoteZenkakuHankaku",
                BackQuoteMode == BackQuoteKeyMode.ZenkakuHankaku);
        }
    }

    public sealed class FcitxCjkInputEntry : Mod {
        internal static FcitxCjkInputSettings Settings { get; private set; }

        public FcitxCjkInputEntry(ModContentPack content) : base(content) {
            Settings = GetSettings<FcitxCjkInputSettings>();
        }

        public override string SettingsCategory() {
            return "Fcitx CJK Input";
        }

        public override void DoSettingsWindowContents(Rect inRect) {
            var listing = new Listing_Standard();
            listing.Begin(inRect);
            var enabled = Settings.DebugLog;
            listing.CheckboxLabeled("Debug log", ref enabled,
                "Write verbose IME diagnostics to /tmp/fcitxcjkinput.log.");
            var forceRightAltHangul = Settings.ForceRightAltHangul;
            listing.CheckboxLabeled("Force RightAlt key to convert 한/영 key(0xFF31)",
                ref forceRightAltHangul,
                "Send the Hangul keysym (0xFF31) to Fcitx when RightAlt key is pressed.");
            var forceBackquoteZenkakuHankaku = Settings.ForceBackquoteZenkakuHankaku;
            listing.CheckboxLabeled("Force ~/` key to convert 半角/全角 key(0xFF2A) : Not Implemented",
                ref forceBackquoteZenkakuHankaku,
                "Send the ZenkakuHankaku keysym (0xFF2A) to Fcitx when the backquote key is pressed.");
            listing.End();

            var settingsChanged = false;
            if (forceRightAltHangul != Settings.ForceRightAltHangul) {
                Settings.ForceRightAltHangul = forceRightAltHangul;
                settingsChanged = true;
            }
            if (forceBackquoteZenkakuHankaku != Settings.ForceBackquoteZenkakuHankaku) {
                Settings.ForceBackquoteZenkakuHankaku = forceBackquoteZenkakuHankaku;
                settingsChanged = true;
            }
            if (settingsChanged)
                WriteSettings();

            if (enabled != Settings.DebugLog) {
                Settings.DebugLog = enabled;
                WriteSettings();
                FcitxCjkInputMod.SetDebugLogging(enabled);
            }
        }
    }
}
