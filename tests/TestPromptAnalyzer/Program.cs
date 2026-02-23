using RevitChat.Services;

var testCases = new (string input, string expIntent, string expCat, string expSys, string expLevel, string expTools)[]
{
    // Count
    ("co bao nhieu ong gio tren tang 2",       "Count",    "Ducts",               null,            "2",  "count_elements"),
    ("how many pipes on Level 3",              "Count",    "Pipes",               null,            "3",  "count_elements"),
    ("dem dau phun tang 1",                    "Count",    "Sprinklers",          null,            "1",  "count_elements"),

    // Analyze
    ("thong ke ong gio cap gio tang 1",        "Analyze",  "Ducts",               "Supply Air",    "1",  "get_duct_summary"),
    ("boq ong nuoc nuoc lanh",                 "Analyze",  "Pipes",               "Chilled Water", null, "get_pipe_summary"),
    ("summary of chilled water pipes",         "Analyze",  "Pipes",               "Chilled Water", null, "get_pipe_summary"),
    ("thong ke chw",                           "Analyze",  null,                  "Chilled Water", null, "calculate_system_totals"),

    // Query
    ("liet ke thiet bi co dien tang 3",        "Query",    "Mechanical Equipment", null,            "3",  "get_mechanical_equipment"),
    ("list all ducts on Level 1",              "Query",    "Ducts",               null,            "1",  "get_elements"),
    ("tim ong nuoc nuoc nong",                 "Query",    "Pipes",               "Hot Water",     null, "get_elements"),
    ("tim phong tren tang 2",                  "Query",    "Rooms",               null,            "2",  "get_rooms"),

    // Check
    ("kiem tra ong nuoc lanh co bi ngat khong","Check",    "Pipes",               "Chilled Water", null, "check_disconnected_elements"),
    ("check velocity cua ong gio cap gio",     "Check",    "Ducts",               "Supply Air",    null, "check_velocity"),
    ("kiem tra do doc ong thoat nuoc",         "Check",    "Pipes",               "Sanitary",      null, "check_pipe_slope"),
    ("kiem tra clash ong nuoc va ong gio",     "Check",    "Pipes",               null,            null, null),
    ("audit model health",                     "Check",    null,                  null,            null, "audit_model_standards"),

    // Export
    ("xuat khoi luong ong gio ra csv",         "Export",   "Ducts",               null,            null, "export_mep_boq"),
    ("export pipe data to xlsx",               "Export",   "Pipes",               null,            null, "export_mep_boq"),

    // Visual
    ("to mau ong gio theo he thong cap gio",   "Visual",   "Ducts",               "Supply Air",    null, "override_color_by_system"),
    ("isolate all pipes on level 2",           "Visual",   "Pipes",               null,            "2",  null),
    ("color duct by system chw",               "Visual",   "Ducts",               "Chilled Water", null, "override_color_by_system"),

    // Navigate
    ("chon tat ca ong gio",                    "Navigate", "Ducts",               null,            null, "select_elements"),
    ("select all pipes level 1",               "Navigate", "Pipes",               null,            "1",  "select_elements"),

    // Tag
    ("tag all ducts on level 1",               "Tag",      "Ducts",               null,            "1",  "get_untagged_elements"),
    ("ghi chu ong nuoc",                       "Tag",      "Pipes",               null,            null, "get_untagged_elements"),

    // Connect
    ("noi ong gio lai",                        "Connect",  "Ducts",               null,            null, "connect_mep_elements"),

    // Delete
    ("xoa tat ca ong gio cu",                  "Delete",   "Ducts",               null,            null, null),

    // Edge: abbreviation systems
    ("check sa ducts",                         "Check",    "Ducts",               "Supply Air",    null, null),

    // Edge: no false positive — "can" should NOT trigger "an" (hide/Visual)
    ("can ong gi day",                         "Unknown",  null,                  null,            null, null),
    ("thong bao cho toi",                      "Unknown",  null,                  null,            null, null),
    ("phong nay dep qua",                      "Unknown",  "Rooms",               null,            null, null),
};

int passed = 0, failed = 0, warned = 0;
Console.WriteLine("╔══════════════════════════════════════════════════════════════╗");
Console.WriteLine("║              PromptAnalyzer Test Suite                      ║");
Console.WriteLine("╚══════════════════════════════════════════════════════════════╝\n");

foreach (var (input, expIntent, expCat, expSys, expLevel, expTool) in testCases)
{
    var ctx = PromptAnalyzer.Analyze(input);
    var actualIntent = ctx.PrimaryIntent.ToString();
    var errors = new List<string>();

    if (actualIntent != expIntent)
        errors.Add($"Intent: expected={expIntent}, got={actualIntent}");

    if (expCat != null && ctx.DetectedCategory != expCat)
        errors.Add($"Category: expected={expCat}, got={ctx.DetectedCategory ?? "null"}");
    else if (expCat == null && ctx.DetectedCategory != null)
        errors.Add($"Category: expected=null, got={ctx.DetectedCategory}");

    if (expSys != null && ctx.DetectedSystem != expSys)
        errors.Add($"System: expected={expSys}, got={ctx.DetectedSystem ?? "null"}");
    else if (expSys == null && ctx.DetectedSystem != null)
        errors.Add($"System: expected=null, got={ctx.DetectedSystem}");

    if (expLevel != null && ctx.DetectedLevel != expLevel)
        errors.Add($"Level: expected={expLevel}, got={ctx.DetectedLevel ?? "null"}");

    if (expTool != null && !ctx.SuggestedTools.Contains(expTool))
        errors.Add($"Tool: expected contains '{expTool}', got=[{string.Join(", ", ctx.SuggestedTools)}]");

    if (errors.Count == 0)
    {
        Console.ForegroundColor = ConsoleColor.Green;
        Console.Write("  PASS ");
        Console.ResetColor();
        Console.WriteLine($"| {input}");
        passed++;
    }
    else
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.Write("  FAIL ");
        Console.ResetColor();
        Console.WriteLine($"| {input}");
        foreach (var e in errors)
            Console.WriteLine($"         -> {e}");
        failed++;
    }
}

Console.WriteLine($"\n══════════════════════════════════════════════════════════════");
var color = failed == 0 ? ConsoleColor.Green : ConsoleColor.Red;
Console.ForegroundColor = color;
Console.WriteLine($"  Results: {passed} passed, {failed} failed / {passed + failed} total");
Console.ResetColor();

if (failed > 0) Environment.Exit(1);
