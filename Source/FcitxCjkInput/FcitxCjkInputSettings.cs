using UnityEngine;
using Verse;

namespace FcitxCjkInput {
    public sealed class FcitxCjkInputSettings : ModSettings {
        public bool DebugLog;
        public RightAltKeyMode RightAltMode = RightAltKeyMode.AltRight;
        public bool ForceRightAltHangul;
        public bool ForceBackquoteEisuToggle;

        public override void ExposeData() {
            Scribe_Values.Look(ref DebugLog, "debugLog", false);
            Scribe_Values.Look(ref RightAltMode, "rightAltMode", RightAltKeyMode.AltRight);
            Scribe_Values.Look(ref ForceRightAltHangul, "forceRightAltHangul",
                RightAltMode == RightAltKeyMode.Hangul);
            Scribe_Values.Look(ref ForceBackquoteEisuToggle, "forceBackquoteEisuToggle",
                RightAltMode == RightAltKeyMode.ZenkakuHankaku);
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
            listing.CheckboxLabeled("Force Right Alt to Hangul toggle",
                ref forceRightAltHangul,
                "Send the Hangul keysym (0xFF31) to Fcitx when Right Alt is pressed.");
            var forceBackquoteEisuToggle = Settings.ForceBackquoteEisuToggle;
            listing.CheckboxLabeled("Map ` / ~ key to Japanese EisuToggle (0xFF30)",
                ref forceBackquoteEisuToggle,
                "Send the EisuToggle keysym (0xFF30) to Fcitx when the backquote key is pressed.");
            listing.End();

            var settingsChanged = false;
            if (forceRightAltHangul != Settings.ForceRightAltHangul) {
                Settings.ForceRightAltHangul = forceRightAltHangul;
                settingsChanged = true;
            }
            if (forceBackquoteEisuToggle != Settings.ForceBackquoteEisuToggle) {
                Settings.ForceBackquoteEisuToggle = forceBackquoteEisuToggle;
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
