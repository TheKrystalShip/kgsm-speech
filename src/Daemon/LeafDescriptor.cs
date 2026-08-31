using TheKrystalShip.KGSM.ComponentConfig;

// What this leaf is, and what it can be configured with. The descriptor generated from these and
// from SpeechOptions is what the Control Panel renders its configuration page from — so a knob is
// declared once, on the property, and never written down again.

[assembly: Leaf(
    id: "speech",
    displayName: "Speech",
    unit: "kgsm-speech.service",
    role: "The host's voice — speech recognition and synthesis for every surface that listens or speaks.",
    // Socket-activated and idle-exiting: inactive is this leaf's resting state, not a fault, and the
    // Control Panel renders it neutrally rather than as "stopped".
    OnDemand = true)]

// Lowest precedence first — the order the daemon itself resolves them in. Without these the panel
// would report every value this leaf runs with as the descriptor's declared default, including the
// four its unit sets, which is a different claim from the one the page is for.
[assembly: ConfigFloorSource("appsettings", "/opt/kgsm-speech/kgsm-speech.settings.json")]
[assembly: ConfigFloorSource("systemd-unit", "kgsm-speech.service")]
[assembly: ConfigFloorSource("env-file", "/etc/kgsm-speech/kgsm-speech.env")]

[assembly: ConfigGroup("voice", "Voice", 1)]
[assembly: ConfigGroup("general", "General", 0)]
[assembly: ConfigGroup("models", "Models", 2)]
// Beside the models, because it is the same subject from the other end: those say what is loaded,
// this says for how long.
[assembly: ConfigGroup("lifetime", "Staying loaded", 3)]
[assembly: ConfigGroup("wiring", "Wiring", 4)]

// ── The runtime's own section, described for this leaf ───────────────────────
// Not a settings type of ours, so it is described here rather than on a property.
[assembly: ConfigFrameworkNamespace("Logging__",
    "per-category filtering is open-ended: any category name is a valid key")]

[assembly: ConfigFrameworkField("logLevel", "Logging__LogLevel__Default", "Log level",
    Description = "Minimum severity this leaf logs.",
    Group = "general",
    Type = ConfigType.Enum,
    Values = ["Trace", "Debug", "Information", "Warning", "Error", "Critical"])]
