using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace RevitChat.Services
{
    /// <summary>
    /// Offline self-training loop: runs predefined prompts through the full analysis pipeline
    /// (PromptAnalyzer → ToolCallEnricher → learning services) to bootstrap the chatbot's
    /// knowledge before any real user interaction.
    /// 
    /// Supports multi-epoch training with augmentation:
    ///   - Epoch 1: Original prompts → establish baseline
    ///   - Epoch 2: Shuffled prompts with noise → test robustness
    ///   - Epoch 3: Augmented variants (typos, abbreviations, mixed lang) → generalize
    ///   - Epoch 4+: Repeat with decaying learning rate → reinforce patterns
    /// 
    /// Also trains on conversation chains (12-15 prompts each) to exercise context
    /// carryover via ConversationContextTracker, covering all probable intent/category/
    /// system/level transitions.
    /// </summary>
    public static class SelfTrainingService
    {
        public static event Action<string> TrainingProgress;
        public static event Action<int, int> TrainingStep;

        private static bool _hasRun;
        private static readonly Random _rng = new(42);

        public static void RunIfNeeded()
        {
            if (_hasRun) return;
            if (DynamicFewShotSelector.ExampleCount > 20 &&
                AdaptiveWeightManager.TotalAdjustments > 10)
            {
                _hasRun = true;
                return;
            }
            _ = Task.Run(() => RunTraining());
        }

        /// <param name="epochs">Number of training passes (default 3).</param>
        /// <param name="augment">Enable prompt augmentation for epochs 2+.</param>
        public static TrainingReport RunTraining(int epochs = 3, bool augment = true,
            EmbeddingMatcher embeddingMatcher = null, CancellationToken ct = default)
        {
            _hasRun = true;
            var sw = Stopwatch.StartNew();
            var report = new TrainingReport();

            var baseDataset = BuildTrainingDataset();
            report.TotalSamples = baseDataset.Count;
            report.Epochs = epochs;

            TrainingProgress?.Invoke($"Self-training: {baseDataset.Count} samples × {epochs} epochs...");

            // ── Phase 1: Individual sample training ──
            for (int epoch = 0; epoch < epochs; epoch++)
            {
                if (ct.IsCancellationRequested) break;

                var dataset = epoch == 0
                    ? baseDataset
                    : PrepareEpochDataset(baseDataset, epoch, augment);

                double decayFactor = 1.0 / (1.0 + epoch * 0.3);

                TrainingProgress?.Invoke($"Epoch {epoch + 1}/{epochs} ({dataset.Count} samples, decay={decayFactor:F2})...");

                for (int i = 0; i < dataset.Count; i++)
                {
                    if (ct.IsCancellationRequested) break;
                    TrainingStep?.Invoke(epoch * baseDataset.Count + i + 1, epochs * baseDataset.Count);

                    try
                    {
                        TrainOnSample(dataset[i], report, decayFactor);
                    }
                    catch (Exception ex)
                    {
                        report.Errors++;
                        Debug.WriteLine($"[SelfTraining] Epoch {epoch + 1}, sample {i}: {ex.Message}");
                    }
                }

                report.EpochResults.Add(new EpochResult
                {
                    Epoch = epoch + 1,
                    Matched = report.Matched,
                    IntentCorrect = report.IntentCorrect,
                    CategoryCorrect = report.CategoryCorrect,
                    ToolCorrect = report.ToolCorrect
                });
            }

            // ── Phase 2: Conversation chain training ──
            var chains = BuildConversationChains();
            report.ConversationChains = chains.Count;
            report.ConversationSteps = chains.Sum(c => c.Steps.Count);

            int convEpochs = Math.Min(epochs, 2);
            report.ConversationTotalProcessed = report.ConversationSteps * convEpochs;

            TrainingProgress?.Invoke(
                $"Conversation training: {chains.Count} chains, {report.ConversationSteps} steps × {convEpochs} epochs...");

            for (int epoch = 0; epoch < convEpochs; epoch++)
            {
                if (ct.IsCancellationRequested) break;

                var ordered = epoch == 0 ? chains : chains.OrderBy(_ => _rng.Next()).ToList();
                double decayFactor = 1.0 / (1.0 + epoch * 0.3);

                TrainingProgress?.Invoke(
                    $"Conv epoch {epoch + 1}/{convEpochs} ({ordered.Count} chains, decay={decayFactor:F2})...");

                foreach (var chain in ordered)
                {
                    if (ct.IsCancellationRequested) break;
                    TrainOnConversation(chain, report, decayFactor);
                }
            }

            // ── Phase 3: Embeddings ──
            if (embeddingMatcher?.IsAvailable == true)
            {
                _ = StoreEmbeddingsAsync(baseDataset, embeddingMatcher, ct);
                report.EmbeddingsQueued = true;
            }

            try { DynamicFewShotSelector.Save(); } catch { }
            try { ProjectContextMemory.Save(); } catch { }

            sw.Stop();
            report.ElapsedMs = sw.ElapsedMilliseconds;

            TrainingProgress?.Invoke(
                $"Training complete: {report.Matched}/{report.TotalSamples * epochs} samples + " +
                $"{report.ConversationMatched}/{report.ConversationSteps * convEpochs} conv steps, " +
                $"{report.FewShotAdded} examples, {report.WeightsAdjusted} weights, {report.ElapsedMs}ms");

            return report;
        }

        private static List<TrainingSample> PrepareEpochDataset(List<TrainingSample> baseData,
            int epochIndex, bool augment)
        {
            var shuffled = baseData.OrderBy(_ => _rng.Next()).ToList();

            if (!augment) return shuffled;

            var augmented = new List<TrainingSample>(shuffled.Count);
            foreach (var sample in shuffled)
            {
                var variant = epochIndex switch
                {
                    1 => AugmentWithTypos(sample),
                    2 => AugmentWithAbbreviations(sample),
                    _ => AugmentWithMixedLanguage(sample)
                };
                augmented.Add(variant ?? sample);
            }
            return augmented;
        }

        private static TrainingSample AugmentWithTypos(TrainingSample s)
        {
            var words = s.Prompt.Split(' ');
            if (words.Length < 3) return s;
            int idx = _rng.Next(1, words.Length);
            var w = words[idx];
            if (w.Length > 3)
            {
                var chars = w.ToCharArray();
                int ci = _rng.Next(1, chars.Length - 1);
                (chars[ci], chars[ci - 1]) = (chars[ci - 1], chars[ci]);
                words[idx] = new string(chars);
            }
            return new TrainingSample(string.Join(" ", words), s.ExpectedIntent,
                s.ExpectedCategory, s.ExpectedTools.ToArray(), s.ExpectedArgs, s.ExpectedSystem);
        }

        private static readonly Dictionary<string, string> AbbrMap = new(StringComparer.OrdinalIgnoreCase)
        {
            ["ducts"] = "dcts", ["pipes"] = "pps", ["elements"] = "elems",
            ["level"] = "lvl", ["system"] = "sys", ["velocity"] = "vel",
            ["insulation"] = "insul", ["diameter"] = "dia",
            ["ống gió"] = "OG", ["ống nước"] = "ON", ["kiểm tra"] = "KT",
            ["tầng"] = "T", ["hệ thống"] = "HT", ["thiết bị"] = "TB",
        };

        private static TrainingSample AugmentWithAbbreviations(TrainingSample s)
        {
            var prompt = s.Prompt;
            foreach (var (full, abbr) in AbbrMap)
            {
                if (prompt.Contains(full, StringComparison.OrdinalIgnoreCase))
                {
                    prompt = prompt.Replace(full, abbr, StringComparison.OrdinalIgnoreCase);
                    break;
                }
            }
            if (prompt == s.Prompt) return s;
            return new TrainingSample(prompt, s.ExpectedIntent, s.ExpectedCategory,
                s.ExpectedTools.ToArray(), s.ExpectedArgs, s.ExpectedSystem);
        }

        private static TrainingSample AugmentWithMixedLanguage(TrainingSample s)
        {
            var prompt = s.Prompt;
            var replacements = new (string en, string vn)[]
            {
                ("ducts", "ống gió"), ("pipes", "ống nước"), ("walls", "tường"),
                ("rooms", "phòng"), ("level", "tầng"), ("check", "kiểm tra"),
                ("list", "liệt kê"), ("count", "đếm"), ("delete", "xóa"),
                ("show", "hiển thị"), ("find", "tìm"), ("export", "xuất"),
                ("ống gió", "ducts"), ("ống nước", "pipes"), ("tường", "walls"),
                ("kiểm tra", "check"), ("liệt kê", "list"), ("đếm", "count"),
            };

            foreach (var (a, b) in replacements)
            {
                if (prompt.Contains(a, StringComparison.OrdinalIgnoreCase))
                {
                    prompt = prompt.Replace(a, b, StringComparison.OrdinalIgnoreCase);
                    break;
                }
            }
            if (prompt == s.Prompt) return s;
            return new TrainingSample(prompt, s.ExpectedIntent, s.ExpectedCategory,
                s.ExpectedTools.ToArray(), s.ExpectedArgs, s.ExpectedSystem);
        }

        private static void TrainOnSample(TrainingSample sample, TrainingReport report, double decay)
        {
            var ctx = PromptAnalyzer.Analyze(sample.Prompt);

            bool intentOk = ctx.PrimaryIntent == sample.ExpectedIntent ||
                            ctx.SecondaryIntent == sample.ExpectedIntent;
            if (intentOk) report.IntentCorrect++;

            bool categoryOk = string.IsNullOrEmpty(sample.ExpectedCategory) ||
                string.Equals(ctx.DetectedCategory, sample.ExpectedCategory, StringComparison.OrdinalIgnoreCase);
            if (categoryOk) report.CategoryCorrect++;

            bool toolOk = sample.ExpectedTools.Count == 0 ||
                sample.ExpectedTools.Any(t => ctx.SuggestedTools.Contains(t));
            if (toolOk) report.ToolCorrect++;

            if (intentOk && categoryOk && toolOk) report.Matched++;

            int repeats = decay > 0.8 ? 2 : 1;
            for (int r = 0; r < repeats; r++)
            {
                foreach (var tool in sample.ExpectedTools)
                {
                    AdaptiveWeightManager.RecordSuccess(
                        sample.ExpectedIntent.ToString(),
                        ExtractMainKeyword(sample.Prompt), tool);

                    DynamicFewShotSelector.RecordSuccess(
                        sample.Prompt, tool, sample.ExpectedArgs,
                        sample.ExpectedIntent.ToString(), sample.ExpectedCategory);
                    report.FewShotAdded++;

                    ProjectContextMemory.RecordToolUsage(tool, sample.ExpectedCategory,
                        sample.ExpectedSystem, sample.ExpectedIntent.ToString());
                }
            }
            report.WeightsAdjusted++;

            if (!intentOk && ctx.PrimaryIntent != PromptIntent.Unknown)
            {
                AdaptiveWeightManager.RecordFailure(
                    ctx.PrimaryIntent.ToString(),
                    ExtractMainKeyword(sample.Prompt));
            }
        }

        // ═════════════════════════════════════════════════════════
        //  CONVERSATION CHAIN TRAINING
        // ═════════════════════════════════════════════════════════

        private static void TrainOnConversation(ConversationChain chain,
            TrainingReport report, double decay)
        {
            var tracker = new ConversationContextTracker();

            foreach (var step in chain.Steps)
            {
                try
                {
                    var ctx = PromptAnalyzer.Analyze(step.Prompt);
                    tracker.ApplyCarryover(ctx, step.Prompt);

                    bool intentOk = ctx.PrimaryIntent == step.ExpectedIntent ||
                                    ctx.SecondaryIntent == step.ExpectedIntent;
                    bool categoryOk = string.IsNullOrEmpty(step.ExpectedCategory) ||
                        string.Equals(ctx.DetectedCategory, step.ExpectedCategory,
                            StringComparison.OrdinalIgnoreCase);
                    bool toolOk = step.ExpectedTools.Count == 0 ||
                        step.ExpectedTools.Any(t => ctx.SuggestedTools.Contains(t));

                    if (intentOk && categoryOk && toolOk)
                        report.ConversationMatched++;

                    int repeats = decay > 0.8 ? 2 : 1;
                    for (int r = 0; r < repeats; r++)
                    {
                        foreach (var tool in step.ExpectedTools)
                        {
                            AdaptiveWeightManager.RecordSuccess(
                                step.ExpectedIntent.ToString(),
                                ExtractMainKeyword(step.Prompt), tool);
                            DynamicFewShotSelector.RecordSuccess(
                                step.Prompt, tool, step.ExpectedArgs,
                                step.ExpectedIntent.ToString(), step.ExpectedCategory);
                            report.FewShotAdded++;
                            ProjectContextMemory.RecordToolUsage(tool, step.ExpectedCategory,
                                step.ExpectedSystem, step.ExpectedIntent.ToString());
                        }
                    }
                    report.WeightsAdjusted++;

                    if (!intentOk && ctx.PrimaryIntent != PromptIntent.Unknown)
                    {
                        AdaptiveWeightManager.RecordFailure(
                            ctx.PrimaryIntent.ToString(),
                            ExtractMainKeyword(step.Prompt));
                    }

                    tracker.Update(ctx);
                }
                catch (Exception ex)
                {
                    report.Errors++;
                    Debug.WriteLine($"[ConvTrain] {chain.Name}: {ex.Message}");
                }
            }
        }

        private static async Task StoreEmbeddingsAsync(List<TrainingSample> dataset,
            EmbeddingMatcher matcher, CancellationToken ct)
        {
            int stored = 0;
            foreach (var sample in dataset)
            {
                if (ct.IsCancellationRequested) break;
                if (sample.ExpectedTools.Count == 0) continue;
                try
                {
                    await matcher.StoreAsync(sample.Prompt, sample.ExpectedTools[0],
                        sample.ExpectedArgs, sample.ExpectedIntent.ToString(), ct);
                    stored++;
                    if (stored % 30 == 0)
                        TrainingProgress?.Invoke($"Embedding storage: {stored}/{dataset.Count}...");
                }
                catch { }
            }
            try { matcher.Save(); } catch { }
            TrainingProgress?.Invoke($"Embeddings stored: {stored}");
        }

        private static string ExtractMainKeyword(string prompt)
        {
            var lower = prompt.ToLowerInvariant();
            var words = lower.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return words
                .Where(w => w.Length > 2 && !StopWords.Contains(w))
                .OrderByDescending(w => w.Length)
                .FirstOrDefault() ?? "";
        }

        private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
        {
            "the", "a", "an", "of", "in", "on", "to", "for", "and", "or", "is", "are",
            "all", "my", "me", "it", "this", "that", "with", "by", "from", "at",
            "toi", "cua", "cac", "va", "cho", "trong", "la", "co", "khong", "voi",
            "hay", "xin", "vui", "long", "giup", "please", "can", "how", "what",
            "hãy", "được", "không", "bao", "nhiêu", "tất", "cả", "mọi", "những"
        };

        // ═════════════════════════════════════════════════════════
        //  TRAINING DATASET — 300+ bilingual samples
        // ═════════════════════════════════════════════════════════
        #region Training Dataset

        private static List<TrainingSample> BuildTrainingDataset()
        {
            var d = new List<TrainingSample>(350);
            AddQuery(d);
            AddCount(d);
            AddAnalyze(d);
            AddCheck(d);
            AddModify(d);
            AddCreate(d);
            AddDelete(d);
            AddExport(d);
            AddVisual(d);
            AddConnect(d);
            AddTag(d);
            AddNavigate(d);
            AddHelp(d);
            AddElectrical(d);
            AddStructural(d);
            AddEnergy(d);
            AddLinkedModels(d);
            AddSheetView(d);
            AddNumericUnit(d);
            AddDryRun(d);
            AddLimitTopN(d);
            AddDirectional(d);
            AddAmbiguous(d);
            AddSystemAbbr(d);
            AddDnDimension(d);
            AddConversational(d);
            return d;
        }

        // ── QUERY ────────────────────────────────────────────────
        private static void AddQuery(List<TrainingSample> d)
        {
            d.Add(new("list all ducts on level 1", PromptIntent.Query, "Ducts", new[] { "get_elements", "get_duct_summary" }));
            d.Add(new("liệt kê tất cả ống gió tầng 1", PromptIntent.Query, "Ducts", new[] { "get_elements", "get_duct_summary" }));
            d.Add(new("show me all pipes in Supply Air system", PromptIntent.Query, "Pipes", new[] { "get_elements" }, system: "Supply Air"));
            d.Add(new("hiển thị ống nước tầng 2", PromptIntent.Query, "Pipes", new[] { "get_elements" }));
            d.Add(new("find all rooms on level B1", PromptIntent.Query, "Rooms", new[] { "get_rooms", "get_rooms_detailed" }));
            d.Add(new("tìm tất cả phòng tầng hầm", PromptIntent.Query, "Rooms", new[] { "get_rooms", "get_rooms_detailed" }));
            d.Add(new("get all mechanical equipment", PromptIntent.Query, "Mechanical Equipment", new[] { "get_mechanical_equipment", "get_elements" }));
            d.Add(new("lấy danh sách thiết bị điện", PromptIntent.Query, "Electrical Equipment", new[] { "get_electrical_equipment", "get_elements" }));
            d.Add(new("show all cable trays level 3", PromptIntent.Query, "Cable Trays", new[] { "get_elements" }));
            d.Add(new("hiển thị khay cáp tầng 3", PromptIntent.Query, "Cable Trays", new[] { "get_elements" }));
            d.Add(new("list sprinklers on level 2", PromptIntent.Query, "Sprinklers", new[] { "get_fire_protection_equipment", "get_elements" }));
            d.Add(new("danh sách đầu phun PCCC tầng 2", PromptIntent.Query, "Sprinklers", new[] { "get_fire_protection_equipment", "get_elements" }));
            d.Add(new("show all air terminals", PromptIntent.Query, "Air Terminals", new[] { "get_elements" }));
            d.Add(new("liệt kê miệng gió", PromptIntent.Query, "Air Terminals", new[] { "get_elements" }));
            d.Add(new("what plumbing fixtures are on level 1", PromptIntent.Query, "Plumbing Fixtures", new[] { "get_plumbing_fixtures", "get_elements" }));
            d.Add(new("thiết bị vệ sinh tầng 1 có gì", PromptIntent.Query, "Plumbing Fixtures", new[] { "get_plumbing_fixtures", "get_elements" }));
            d.Add(new("list conduits on level 2", PromptIntent.Query, "Conduits", new[] { "get_elements" }));
            d.Add(new("liệt kê ống dẫn tầng 2", PromptIntent.Query, "Conduits", new[] { "get_elements" }));
            d.Add(new("show all lighting fixtures", PromptIntent.Query, "Lighting Fixtures", new[] { "get_elements" }));
            d.Add(new("hiển thị tất cả đèn", PromptIntent.Query, "Lighting Fixtures", new[] { "get_elements" }));
            d.Add(new("get duct fittings on level 1", PromptIntent.Query, "Duct Fittings", new[] { "get_elements" }));
            d.Add(new("xem phụ kiện ống gió tầng 1", PromptIntent.Query, "Duct Fittings", new[] { "get_elements" }));
            d.Add(new("show pipe accessories", PromptIntent.Query, "Pipe Accessories", new[] { "get_elements" }));
            d.Add(new("xem phụ kiện ống nước", PromptIntent.Query, "Pipe Accessories", new[] { "get_elements" }));
            d.Add(new("what FCU are on level 3", PromptIntent.Query, "Mechanical Equipment", new[] { "get_mechanical_equipment", "get_elements" }));
            d.Add(new("tầng 3 có FCU nào", PromptIntent.Query, "Mechanical Equipment", new[] { "get_mechanical_equipment", "get_elements" }));
            d.Add(new("list all columns on level 1", PromptIntent.Query, "Columns", new[] { "get_elements" }));
            d.Add(new("danh sách cột tầng 1", PromptIntent.Query, "Columns", new[] { "get_elements" }));
            d.Add(new("show beams on level 2", PromptIntent.Query, "Structural Framing", new[] { "get_elements" }));
            d.Add(new("xem dầm tầng 2", PromptIntent.Query, "Structural Framing", new[] { "get_elements" }));
            d.Add(new("find ceilings on level 1", PromptIntent.Query, "Ceilings", new[] { "get_elements" }));
            d.Add(new("tìm trần tầng 1", PromptIntent.Query, "Ceilings", new[] { "get_elements" }));
            d.Add(new("list all floors", PromptIntent.Query, "Floors", new[] { "get_elements" }));
            d.Add(new("liệt kê sàn", PromptIntent.Query, "Floors", new[] { "get_elements" }));
            d.Add(new("show all doors on level 1", PromptIntent.Query, "Doors", new[] { "get_elements" }));
            d.Add(new("cửa đi tầng 1", PromptIntent.Query, "Doors", new[] { "get_elements" }));
            d.Add(new("list windows on level 2", PromptIntent.Query, "Windows", new[] { "get_elements" }));
            d.Add(new("cửa sổ tầng 2 có bao nhiêu", PromptIntent.Query, "Windows", new[] { "get_elements" }));
            d.Add(new("get furniture on level 1", PromptIntent.Query, "Furniture", new[] { "get_elements" }));
            d.Add(new("nội thất tầng 1", PromptIntent.Query, "Furniture", new[] { "get_elements" }));
            d.Add(new("find stairs in the building", PromptIntent.Query, "Stairs", new[] { "get_elements" }));
            d.Add(new("tìm cầu thang", PromptIntent.Query, "Stairs", new[] { "get_elements" }));
        }

        // ── COUNT ────────────────────────────────────────────────
        private static void AddCount(List<TrainingSample> d)
        {
            d.Add(new("how many ducts on level 1", PromptIntent.Count, "Ducts", new[] { "count_elements" }));
            d.Add(new("bao nhiêu ống gió tầng 1", PromptIntent.Count, "Ducts", new[] { "count_elements" }));
            d.Add(new("count all pipes", PromptIntent.Count, "Pipes", new[] { "count_elements" }));
            d.Add(new("đếm ống nước", PromptIntent.Count, "Pipes", new[] { "count_elements" }));
            d.Add(new("how many rooms are there", PromptIntent.Count, "Rooms", new[] { "count_elements" }));
            d.Add(new("có bao nhiêu phòng", PromptIntent.Count, "Rooms", new[] { "count_elements" }));
            d.Add(new("count walls on level 2", PromptIntent.Count, "Walls", new[] { "count_elements" }));
            d.Add(new("số lượng tường tầng 2", PromptIntent.Count, "Walls", new[] { "count_elements" }));
            d.Add(new("how many conduits on level 3", PromptIntent.Count, "Conduits", new[] { "count_elements" }));
            d.Add(new("đếm ống dẫn tầng 3", PromptIntent.Count, "Conduits", new[] { "count_elements" }));
            d.Add(new("count cable trays", PromptIntent.Count, "Cable Trays", new[] { "count_elements" }));
            d.Add(new("đếm khay cáp", PromptIntent.Count, "Cable Trays", new[] { "count_elements" }));
            d.Add(new("how many doors on level 1", PromptIntent.Count, "Doors", new[] { "count_elements" }));
            d.Add(new("bao nhiêu cửa tầng 1", PromptIntent.Count, "Doors", new[] { "count_elements" }));
            d.Add(new("count sprinklers on all levels", PromptIntent.Count, "Sprinklers", new[] { "count_elements" }));
            d.Add(new("đếm đầu phun tất cả tầng", PromptIntent.Count, "Sprinklers", new[] { "count_elements" }));
            d.Add(new("how many air terminals on level 2", PromptIntent.Count, "Air Terminals", new[] { "count_elements" }));
            d.Add(new("bao nhiêu miệng gió tầng 2", PromptIntent.Count, "Air Terminals", new[] { "count_elements" }));
        }

        // ── ANALYZE ──────────────────────────────────────────────
        private static void AddAnalyze(List<TrainingSample> d)
        {
            d.Add(new("duct summary for Supply Air system", PromptIntent.Analyze, "Ducts", new[] { "get_duct_summary", "calculate_system_totals" }, system: "Supply Air"));
            d.Add(new("thống kê ống gió cấp gió", PromptIntent.Analyze, "Ducts", new[] { "get_duct_summary", "calculate_system_totals" }, system: "Supply Air"));
            d.Add(new("pipe quantity takeoff level 1", PromptIntent.Analyze, "Pipes", new[] { "mep_quantity_takeoff", "get_pipe_summary" }));
            d.Add(new("bóc khối lượng ống nước tầng 1", PromptIntent.Analyze, "Pipes", new[] { "mep_quantity_takeoff", "get_pipe_summary" }));
            d.Add(new("BOQ for all MEP systems", PromptIntent.Analyze, null, new[] { "mep_quantity_takeoff", "calculate_system_totals" }));
            d.Add(new("tổng hợp khối lượng hệ thống MEP", PromptIntent.Analyze, null, new[] { "mep_quantity_takeoff", "calculate_system_totals" }));
            d.Add(new("analyze chilled water system totals", PromptIntent.Analyze, null, new[] { "calculate_system_totals" }, system: "Chilled Water"));
            d.Add(new("phân tích tổng hệ thống nước lạnh", PromptIntent.Analyze, null, new[] { "calculate_system_totals" }, system: "Chilled Water"));
            d.Add(new("conduit summary level 2", PromptIntent.Analyze, "Conduits", new[] { "get_conduit_summary", "mep_quantity_takeoff" }));
            d.Add(new("thống kê ống dẫn tầng 2", PromptIntent.Analyze, "Conduits", new[] { "get_conduit_summary", "mep_quantity_takeoff" }));
            d.Add(new("cable tray takeoff", PromptIntent.Analyze, "Cable Trays", new[] { "get_cable_tray_summary", "mep_quantity_takeoff" }));
            d.Add(new("bóc khối lượng khay cáp", PromptIntent.Analyze, "Cable Trays", new[] { "get_cable_tray_summary", "mep_quantity_takeoff" }));
            d.Add(new("tóm tắt hệ thống exhaust air", PromptIntent.Analyze, null, new[] { "calculate_system_totals" }, system: "Exhaust Air"));
            d.Add(new("return air system summary", PromptIntent.Analyze, null, new[] { "calculate_system_totals" }, system: "Return Air"));
            d.Add(new("báo giá ống gió tầng 1", PromptIntent.Analyze, "Ducts", new[] { "mep_quantity_takeoff", "get_duct_summary" }));
            d.Add(new("classify elements by system", PromptIntent.Analyze, null, new[] { "calculate_system_totals" }));
        }

        // ── CHECK ────────────────────────────────────────────────
        private static void AddCheck(List<TrainingSample> d)
        {
            d.Add(new("check duct velocity", PromptIntent.Check, "Ducts", new[] { "check_velocity" }));
            d.Add(new("kiểm tra vận tốc ống gió", PromptIntent.Check, "Ducts", new[] { "check_velocity" }));
            d.Add(new("check pipe slope", PromptIntent.Check, "Pipes", new[] { "check_pipe_slope" }));
            d.Add(new("kiểm tra độ dốc ống nước", PromptIntent.Check, "Pipes", new[] { "check_pipe_slope" }));
            d.Add(new("audit model for warnings", PromptIntent.Check, null, new[] { "audit_model_standards", "get_model_warnings" }));
            d.Add(new("kiểm tra cảnh báo model", PromptIntent.Check, null, new[] { "audit_model_standards", "get_model_warnings" }));
            d.Add(new("check for disconnected elements", PromptIntent.Check, null, new[] { "check_disconnected_elements" }));
            d.Add(new("kiểm tra phần tử ngắt kết nối", PromptIntent.Check, null, new[] { "check_disconnected_elements" }));
            d.Add(new("check insulation coverage", PromptIntent.Check, null, new[] { "check_insulation_coverage" }));
            d.Add(new("kiểm tra bảo ôn", PromptIntent.Check, null, new[] { "check_insulation_coverage" }));
            d.Add(new("run clash detection", PromptIntent.Check, null, new[] { "check_clashes", "get_clash_summary" }));
            d.Add(new("kiểm tra va chạm", PromptIntent.Check, null, new[] { "check_clashes", "get_clash_summary" }));
            d.Add(new("check velocity max 5 m/s", PromptIntent.Check, null, new[] { "check_velocity" }, args: A("max_velocity_ms", 5.0)));
            d.Add(new("kiểm tra vận tốc tối đa 5 m/s", PromptIntent.Check, null, new[] { "check_velocity" }, args: A("max_velocity_ms", 5.0)));
            d.Add(new("verify pipe connections on level 1", PromptIntent.Check, "Pipes", new[] { "check_disconnected_elements" }));
            d.Add(new("xác nhận kết nối ống tầng 1", PromptIntent.Check, "Pipes", new[] { "check_disconnected_elements" }));
            d.Add(new("validate duct sizes for exhaust", PromptIntent.Check, "Ducts", new[] { "check_velocity" }, system: "Exhaust Air"));
            d.Add(new("đánh giá kích thước ống hút", PromptIntent.Check, "Ducts", new[] { "check_velocity" }, system: "Exhaust Air"));
            d.Add(new("are there any missing insulation", PromptIntent.Check, null, new[] { "check_insulation_coverage" }));
            d.Add(new("có chỗ nào thiếu bảo ôn không", PromptIntent.Check, null, new[] { "check_insulation_coverage" }));
            d.Add(new("check model health", PromptIntent.Check, null, new[] { "audit_model_standards" }));
            d.Add(new("kiểm tra sức khỏe model", PromptIntent.Check, null, new[] { "audit_model_standards" }));
        }

        // ── MODIFY ───────────────────────────────────────────────
        private static void AddModify(List<TrainingSample> d)
        {
            d.Add(new("resize ducts to 300mm", PromptIntent.Modify, "Ducts", new[] { "resize_mep_elements" }, args: A("diameter_mm", 300)));
            d.Add(new("đổi kích thước ống gió sang 300mm", PromptIntent.Modify, "Ducts", new[] { "resize_mep_elements" }, args: A("diameter_mm", 300)));
            d.Add(new("set pipe slope to 2%", PromptIntent.Modify, "Pipes", new[] { "set_pipe_slope" }, args: A("slope_pct", 2.0)));
            d.Add(new("đặt độ dốc ống nước 2%", PromptIntent.Modify, "Pipes", new[] { "set_pipe_slope" }, args: A("slope_pct", 2.0)));
            d.Add(new("split pipes every 1350mm", PromptIntent.Modify, "Pipes", new[] { "split_mep_elements" }, args: A("segment_length_mm", 1350)));
            d.Add(new("chia ống nước mỗi 1350mm", PromptIntent.Modify, "Pipes", new[] { "split_mep_elements" }, args: A("segment_length_mm", 1350)));
            d.Add(new("move selected elements up 500mm", PromptIntent.Modify, null, new[] { "move_elements" }));
            d.Add(new("di chuyển phần tử đang chọn lên 500mm", PromptIntent.Modify, null, new[] { "move_elements" }));
            d.Add(new("add 25mm insulation to pipes", PromptIntent.Modify, "Pipes", new[] { "add_change_insulation" }, args: A("thickness_mm", 25)));
            d.Add(new("thêm bảo ôn 25mm cho ống nước", PromptIntent.Modify, "Pipes", new[] { "add_change_insulation" }, args: A("thickness_mm", 25)));
            d.Add(new("auto size duct at 4 m/s", PromptIntent.Modify, "Ducts", new[] { "auto_size_mep" }, args: A("target_velocity_ms", 4.0)));
            d.Add(new("tự động size ống gió vận tốc 4 m/s", PromptIntent.Modify, "Ducts", new[] { "auto_size_mep" }, args: A("target_velocity_ms", 4.0)));
            d.Add(new("preview resize ducts to 500x300", PromptIntent.Modify, "Ducts", new[] { "resize_mep_elements" }, args: A2("width_mm", 500, "height_mm", 300)));
            d.Add(new("xem trước đổi kích thước ống gió 500x300", PromptIntent.Modify, "Ducts", new[] { "resize_mep_elements" }, args: A2("width_mm", 500, "height_mm", 300)));
            d.Add(new("rename all ducts Mark to D-", PromptIntent.Modify, "Ducts", new[] { "rename_elements" }));
            d.Add(new("đổi tên Mark ống gió thành D-", PromptIntent.Modify, "Ducts", new[] { "rename_elements" }));
            d.Add(new("copy selected elements to level 2", PromptIntent.Modify, null, new[] { "copy_elements" }));
            d.Add(new("sao chép phần tử sang tầng 2", PromptIntent.Modify, null, new[] { "copy_elements" }));
            d.Add(new("mirror selected elements", PromptIntent.Modify, null, new[] { "mirror_elements" }));
            d.Add(new("lật gương phần tử đang chọn", PromptIntent.Modify, null, new[] { "mirror_elements" }));
            d.Add(new("flip pipe direction", PromptIntent.Modify, "Pipes", new[] { "flip_mep_elements" }));
            d.Add(new("đảo chiều ống nước", PromptIntent.Modify, "Pipes", new[] { "flip_mep_elements" }));
            d.Add(new("change duct system to Return Air", PromptIntent.Modify, "Ducts", new[] { "change_mep_system_type" }, system: "Return Air"));
            d.Add(new("đổi hệ thống ống gió sang hồi gió", PromptIntent.Modify, "Ducts", new[] { "change_mep_system_type" }, system: "Return Air"));
            d.Add(new("set offset to 3000mm for pipes", PromptIntent.Modify, "Pipes", new[] { "batch_set_offset" }, args: A("offset_mm", 3000)));
            d.Add(new("đặt offset ống nước 3000mm", PromptIntent.Modify, "Pipes", new[] { "batch_set_offset" }, args: A("offset_mm", 3000)));
            d.Add(new("undo last action", PromptIntent.Modify, null, new[] { "undo_last_action" }));
            d.Add(new("hoàn tác", PromptIntent.Modify, null, new[] { "undo_last_action" }));
        }

        // ── CREATE ───────────────────────────────────────────────
        private static void AddCreate(List<TrainingSample> d)
        {
            d.Add(new("create a wall from point A to B", PromptIntent.Create, "Walls", new[] { "create_element" }));
            d.Add(new("tạo tường từ điểm A đến B", PromptIntent.Create, "Walls", new[] { "create_element" }));
            d.Add(new("add a door to the wall", PromptIntent.Create, "Doors", new[] { "create_element" }));
            d.Add(new("thêm cửa vào tường", PromptIntent.Create, "Doors", new[] { "create_element" }));
            d.Add(new("create a floor on level 1", PromptIntent.Create, "Floors", new[] { "create_element" }));
            d.Add(new("tạo sàn tầng 1", PromptIntent.Create, "Floors", new[] { "create_element" }));
            d.Add(new("add window to wall", PromptIntent.Create, "Windows", new[] { "create_element" }));
            d.Add(new("thêm cửa sổ vào tường", PromptIntent.Create, "Windows", new[] { "create_element" }));
            d.Add(new("create a new room on level 2", PromptIntent.Create, "Rooms", new[] { "create_element" }));
            d.Add(new("tạo phòng mới tầng 2", PromptIntent.Create, "Rooms", new[] { "create_element" }));
        }

        // ── DELETE ───────────────────────────────────────────────
        private static void AddDelete(List<TrainingSample> d)
        {
            d.Add(new("delete selected elements", PromptIntent.Delete, null, new[] { "delete_elements" }));
            d.Add(new("xóa phần tử đang chọn", PromptIntent.Delete, null, new[] { "delete_elements" }));
            d.Add(new("purge unused families", PromptIntent.Delete, null, new[] { "delete_elements" }));
            d.Add(new("dọn dẹp family không sử dụng", PromptIntent.Delete, null, new[] { "delete_elements" }));
            d.Add(new("remove all duct accessories on level 1", PromptIntent.Delete, "Duct Accessories", new[] { "delete_elements" }));
            d.Add(new("xóa phụ kiện ống gió tầng 1", PromptIntent.Delete, "Duct Accessories", new[] { "delete_elements" }));
            d.Add(new("delete all unused views", PromptIntent.Delete, null, new[] { "delete_elements" }));
            d.Add(new("xóa view không dùng", PromptIntent.Delete, null, new[] { "delete_elements" }));
        }

        // ── EXPORT ───────────────────────────────────────────────
        private static void AddExport(List<TrainingSample> d)
        {
            d.Add(new("export ducts to CSV", PromptIntent.Export, "Ducts", new[] { "export_to_csv", "export_mep_boq" }));
            d.Add(new("xuất ống gió ra CSV", PromptIntent.Export, "Ducts", new[] { "export_to_csv", "export_mep_boq" }));
            d.Add(new("export pipe BOQ to xlsx", PromptIntent.Export, "Pipes", new[] { "export_mep_boq", "export_to_csv" }));
            d.Add(new("xuất bóc khối lượng ống nước", PromptIntent.Export, "Pipes", new[] { "export_mep_boq", "export_to_csv" }));
            d.Add(new("export model to IFC", PromptIntent.Export, null, new[] { "export_ifc" }));
            d.Add(new("xuất file IFC", PromptIntent.Export, null, new[] { "export_ifc" }));
            d.Add(new("export rooms to JSON", PromptIntent.Export, "Rooms", new[] { "export_to_json" }));
            d.Add(new("xuất danh sách phòng ra JSON", PromptIntent.Export, "Rooms", new[] { "export_to_json" }));
            d.Add(new("save equipment data to csv", PromptIntent.Export, "Mechanical Equipment", new[] { "export_to_csv" }));
            d.Add(new("lưu dữ liệu thiết bị ra csv", PromptIntent.Export, "Mechanical Equipment", new[] { "export_to_csv" }));
            d.Add(new("export to PDF", PromptIntent.Export, null, new[] { "export_pdf" }));
            d.Add(new("xuất PDF", PromptIntent.Export, null, new[] { "export_pdf" }));
            d.Add(new("export gbXML", PromptIntent.Export, null, new[] { "export_gbxml" }));
            d.Add(new("xuất gbXML", PromptIntent.Export, null, new[] { "export_gbxml" }));
        }

        // ── VISUAL ───────────────────────────────────────────────
        private static void AddVisual(List<TrainingSample> d)
        {
            d.Add(new("color ducts by system", PromptIntent.Visual, "Ducts", new[] { "override_color_by_system" }));
            d.Add(new("tô màu ống gió theo hệ thống", PromptIntent.Visual, "Ducts", new[] { "override_color_by_system" }));
            d.Add(new("hide all walls", PromptIntent.Visual, "Walls", new[] { "hide_category" }));
            d.Add(new("ẩn tất cả tường", PromptIntent.Visual, "Walls", new[] { "hide_category" }));
            d.Add(new("isolate pipes on level 1", PromptIntent.Visual, "Pipes", new[] { "isolate_category" }));
            d.Add(new("cô lập ống nước tầng 1", PromptIntent.Visual, "Pipes", new[] { "isolate_category" }));
            d.Add(new("color elements by parameter Mark", PromptIntent.Visual, null, new[] { "override_color_by_parameter" }));
            d.Add(new("tô màu theo tham số Mark", PromptIntent.Visual, null, new[] { "override_color_by_parameter" }));
            d.Add(new("set transparency 50% for ducts", PromptIntent.Visual, "Ducts", new[] { "override_category_color" }, args: A("transparency", 50)));
            d.Add(new("đặt trong suốt 50% cho ống gió", PromptIntent.Visual, "Ducts", new[] { "override_category_color" }, args: A("transparency", 50)));
            d.Add(new("unhide all elements", PromptIntent.Visual, null, new[] { "reset_view_isolation" }));
            d.Add(new("hiện tất cả phần tử", PromptIntent.Visual, null, new[] { "reset_view_isolation" }));
            d.Add(new("isolate level 1 elements", PromptIntent.Visual, null, new[] { "isolate_by_level" }));
            d.Add(new("cô lập phần tử tầng 1", PromptIntent.Visual, null, new[] { "isolate_by_level" }));
            d.Add(new("color pipes red", PromptIntent.Visual, "Pipes", new[] { "override_category_color" }));
            d.Add(new("tô màu ống nước đỏ", PromptIntent.Visual, "Pipes", new[] { "override_category_color" }));
            d.Add(new("hide ceiling", PromptIntent.Visual, "Ceilings", new[] { "hide_category" }));
            d.Add(new("ẩn trần", PromptIntent.Visual, "Ceilings", new[] { "hide_category" }));
            d.Add(new("filter by Supply Air system", PromptIntent.Visual, null, new[] { "override_color_by_system" }, system: "Supply Air"));
            d.Add(new("lọc theo hệ cấp gió", PromptIntent.Visual, null, new[] { "override_color_by_system" }, system: "Supply Air"));
        }

        // ── CONNECT ──────────────────────────────────────────────
        private static void AddConnect(List<TrainingSample> d)
        {
            d.Add(new("connect selected ducts", PromptIntent.Connect, "Ducts", new[] { "connect_mep_elements" }));
            d.Add(new("nối ống gió đang chọn", PromptIntent.Connect, "Ducts", new[] { "connect_mep_elements" }));
            d.Add(new("route pipe between 2 elements", PromptIntent.Connect, "Pipes", new[] { "route_mep_between" }));
            d.Add(new("nối 2 ống nước đang chọn", PromptIntent.Connect, "Pipes", new[] { "connect_mep_elements" }));
            d.Add(new("connect 2 selected ducts, avoid structural", PromptIntent.Connect, "Ducts", new[] { "connect_mep_elements", "route_mep_between" }));
            d.Add(new("nối 2 ống gió đang chọn, tránh kết cấu", PromptIntent.Connect, "Ducts", new[] { "connect_mep_elements", "route_mep_between" }));
            d.Add(new("route duct from FCU to main", PromptIntent.Connect, "Ducts", new[] { "route_mep_between" }));
            d.Add(new("nối ống gió từ FCU ra ống chính", PromptIntent.Connect, "Ducts", new[] { "route_mep_between" }));
            d.Add(new("connect pipe to equipment", PromptIntent.Connect, "Pipes", new[] { "connect_mep_elements" }));
            d.Add(new("kết nối ống nước vào thiết bị", PromptIntent.Connect, "Pipes", new[] { "connect_mep_elements" }));
        }

        // ── TAG ──────────────────────────────────────────────────
        private static void AddTag(List<TrainingSample> d)
        {
            d.Add(new("tag all untagged ducts", PromptIntent.Tag, "Ducts", new[] { "get_untagged_elements", "tag_all_in_view" }));
            d.Add(new("ghi chú tất cả ống gió chưa tag", PromptIntent.Tag, "Ducts", new[] { "get_untagged_elements", "tag_all_in_view" }));
            d.Add(new("find untagged elements", PromptIntent.Tag, null, new[] { "get_untagged_elements" }));
            d.Add(new("tìm phần tử chưa được tag", PromptIntent.Tag, null, new[] { "get_untagged_elements" }));
            d.Add(new("tag all pipes in view", PromptIntent.Tag, "Pipes", new[] { "tag_all_in_view" }));
            d.Add(new("tag tất cả ống nước trong view", PromptIntent.Tag, "Pipes", new[] { "tag_all_in_view" }));
            d.Add(new("annotate equipment on level 1", PromptIntent.Tag, "Mechanical Equipment", new[] { "get_untagged_elements", "tag_all_in_view" }));
            d.Add(new("ghi chú thiết bị tầng 1", PromptIntent.Tag, "Mechanical Equipment", new[] { "get_untagged_elements", "tag_all_in_view" }));
        }

        // ── NAVIGATE ─────────────────────────────────────────────
        private static void AddNavigate(List<TrainingSample> d)
        {
            d.Add(new("select all ducts", PromptIntent.Navigate, "Ducts", new[] { "select_elements" }));
            d.Add(new("chọn tất cả ống gió", PromptIntent.Navigate, "Ducts", new[] { "select_elements" }));
            d.Add(new("zoom to element 123456", PromptIntent.Navigate, null, new[] { "zoom_to_elements" }));
            d.Add(new("phóng to phần tử 123456", PromptIntent.Navigate, null, new[] { "zoom_to_elements" }));
            d.Add(new("select pipes on level 1", PromptIntent.Navigate, "Pipes", new[] { "select_elements" }));
            d.Add(new("chọn ống nước tầng 1", PromptIntent.Navigate, "Pipes", new[] { "select_elements" }));
            d.Add(new("highlight disconnected elements", PromptIntent.Navigate, null, new[] { "select_elements" }));
            d.Add(new("đánh dấu phần tử bị ngắt", PromptIntent.Navigate, null, new[] { "select_elements" }));
            d.Add(new("go to room 101", PromptIntent.Navigate, "Rooms", new[] { "zoom_to_elements" }));
            d.Add(new("đi đến phòng 101", PromptIntent.Navigate, "Rooms", new[] { "zoom_to_elements" }));
        }

        // ── HELP ─────────────────────────────────────────────────
        private static void AddHelp(List<TrainingSample> d)
        {
            d.Add(new("what can you do", PromptIntent.Help, null, Array.Empty<string>()));
            d.Add(new("bạn có thể làm gì", PromptIntent.Help, null, Array.Empty<string>()));
            d.Add(new("help me with ducts", PromptIntent.Help, "Ducts", Array.Empty<string>()));
            d.Add(new("hướng dẫn sử dụng", PromptIntent.Help, null, Array.Empty<string>()));
            d.Add(new("list all commands", PromptIntent.Help, null, Array.Empty<string>()));
            d.Add(new("danh sách lệnh", PromptIntent.Help, null, Array.Empty<string>()));
            d.Add(new("how to use this tool", PromptIntent.Help, null, Array.Empty<string>()));
            d.Add(new("cách dùng chatbot này", PromptIntent.Help, null, Array.Empty<string>()));
            d.Add(new("what are your capabilities", PromptIntent.Help, null, Array.Empty<string>()));
            d.Add(new("khả năng của bạn là gì", PromptIntent.Help, null, Array.Empty<string>()));
        }

        // ── ELECTRICAL ───────────────────────────────────────────
        private static void AddElectrical(List<TrainingSample> d)
        {
            d.Add(new("show panel schedules", PromptIntent.Query, "Electrical Equipment", new[] { "get_panel_schedules" }));
            d.Add(new("hiển thị bảng panel", PromptIntent.Query, "Electrical Equipment", new[] { "get_panel_schedules" }));
            d.Add(new("check panel capacity", PromptIntent.Check, "Electrical Equipment", new[] { "check_panel_capacity" }));
            d.Add(new("kiểm tra dung lượng tủ điện", PromptIntent.Check, "Electrical Equipment", new[] { "check_panel_capacity" }));
            d.Add(new("get circuit loads", PromptIntent.Query, "Electrical Equipment", new[] { "get_circuit_loads" }));
            d.Add(new("lấy tải mạch", PromptIntent.Query, "Electrical Equipment", new[] { "get_circuit_loads" }));
            d.Add(new("check voltage drop", PromptIntent.Check, "Electrical Equipment", new[] { "get_voltage_drop" }));
            d.Add(new("kiểm tra sụt áp", PromptIntent.Check, "Electrical Equipment", new[] { "get_voltage_drop" }));
            d.Add(new("phase balance analysis", PromptIntent.Analyze, "Electrical Equipment", new[] { "get_phase_balance" }));
            d.Add(new("phân tích cân bằng pha", PromptIntent.Analyze, "Electrical Equipment", new[] { "get_phase_balance" }));
        }

        // ── STRUCTURAL ───────────────────────────────────────────
        private static void AddStructural(List<TrainingSample> d)
        {
            d.Add(new("show structural model", PromptIntent.Query, null, new[] { "get_structural_model" }));
            d.Add(new("hiển thị mô hình kết cấu", PromptIntent.Query, null, new[] { "get_structural_model" }));
            d.Add(new("check rebar coverage", PromptIntent.Check, null, new[] { "check_rebar_coverage" }));
            d.Add(new("kiểm tra lớp bảo vệ cốt thép", PromptIntent.Check, null, new[] { "check_rebar_coverage" }));
            d.Add(new("get rebar schedule level 1", PromptIntent.Query, null, new[] { "get_rebar_schedule" }));
            d.Add(new("bảng thống kê thép tầng 1", PromptIntent.Query, null, new[] { "get_rebar_schedule" }));
            d.Add(new("check foundation loads", PromptIntent.Check, null, new[] { "check_foundation_loads" }));
            d.Add(new("kiểm tra tải móng", PromptIntent.Check, null, new[] { "check_foundation_loads" }));
        }

        // ── ENERGY ───────────────────────────────────────────────
        private static void AddEnergy(List<TrainingSample> d)
        {
            d.Add(new("get view schedules", PromptIntent.Query, null, new[] { "get_view_schedules" }));
            d.Add(new("lấy bảng thống kê", PromptIntent.Query, null, new[] { "get_view_schedules" }));
            d.Add(new("export gbXML for energy analysis", PromptIntent.Export, null, new[] { "export_gbxml" }));
            d.Add(new("xuất gbXML phân tích năng lượng", PromptIntent.Export, null, new[] { "export_gbxml" }));
            d.Add(new("get space energy data", PromptIntent.Query, null, new[] { "get_space_energy_data" }));
            d.Add(new("dữ liệu năng lượng không gian", PromptIntent.Query, null, new[] { "get_space_energy_data" }));
        }

        // ── LINKED MODELS ────────────────────────────────────────
        private static void AddLinkedModels(List<TrainingSample> d)
        {
            d.Add(new("show linked models", PromptIntent.Query, null, new[] { "get_linked_models" }));
            d.Add(new("hiển thị model liên kết", PromptIntent.Query, null, new[] { "get_linked_models" }));
            d.Add(new("count elements in linked model", PromptIntent.Count, null, new[] { "count_linked_elements" }));
            d.Add(new("đếm phần tử trong model link", PromptIntent.Count, null, new[] { "count_linked_elements" }));
            d.Add(new("get elements from linked model", PromptIntent.Query, null, new[] { "get_linked_elements" }));
            d.Add(new("lấy phần tử từ model liên kết", PromptIntent.Query, null, new[] { "get_linked_elements" }));
        }

        // ── SHEET/VIEW ───────────────────────────────────────────
        private static void AddSheetView(List<TrainingSample> d)
        {
            d.Add(new("create 3D view for supply air", PromptIntent.Visual, null, new[] { "create_3d_view_by_system" }, system: "Supply Air"));
            d.Add(new("tạo view 3D cho hệ cấp gió", PromptIntent.Visual, null, new[] { "create_3d_view_by_system" }, system: "Supply Air"));
            d.Add(new("create section view", PromptIntent.Visual, null, new[] { "create_section_view" }));
            d.Add(new("tạo mặt cắt", PromptIntent.Visual, null, new[] { "create_section_view" }));
            d.Add(new("duplicate current view", PromptIntent.Modify, null, new[] { "duplicate_views" }));
            d.Add(new("nhân bản view hiện tại", PromptIntent.Modify, null, new[] { "duplicate_views" }));
            d.Add(new("screenshot current view", PromptIntent.Visual, null, new[] { "screenshot_view" }));
            d.Add(new("chụp ảnh view hiện tại", PromptIntent.Visual, null, new[] { "screenshot_view" }));
            d.Add(new("set view range 0 to 3000mm", PromptIntent.Visual, null, new[] { "set_view_range" }));
            d.Add(new("đặt view range 0 đến 3000mm", PromptIntent.Visual, null, new[] { "set_view_range" }));
            d.Add(new("compare two views", PromptIntent.Visual, null, new[] { "compare_views" }));
            d.Add(new("so sánh hai view", PromptIntent.Visual, null, new[] { "compare_views" }));
        }

        // ── NUMERIC + UNIT ───────────────────────────────────────
        private static void AddNumericUnit(List<TrainingSample> d)
        {
            d.Add(new("check velocity max 2.5 m/s on level 1", PromptIntent.Check, null, new[] { "check_velocity" }, args: A("max_velocity_ms", 2.5)));
            d.Add(new("kiểm tra vận tốc tối đa 2.5 m/s tầng 1", PromptIntent.Check, null, new[] { "check_velocity" }, args: A("max_velocity_ms", 2.5)));
            d.Add(new("resize pipe to 150mm diameter", PromptIntent.Modify, "Pipes", new[] { "resize_mep_elements" }, args: A("diameter_mm", 150)));
            d.Add(new("đổi đường kính ống nước sang 150mm", PromptIntent.Modify, "Pipes", new[] { "resize_mep_elements" }, args: A("diameter_mm", 150)));
            d.Add(new("set insulation 30mm", PromptIntent.Modify, null, new[] { "add_change_insulation" }, args: A("thickness_mm", 30)));
            d.Add(new("bảo ôn 30mm", PromptIntent.Modify, null, new[] { "add_change_insulation" }, args: A("thickness_mm", 30)));
            d.Add(new("offset pipes 2500mm", PromptIntent.Modify, "Pipes", new[] { "batch_set_offset" }, args: A("offset_mm", 2500)));
            d.Add(new("offset ống nước 2500mm", PromptIntent.Modify, "Pipes", new[] { "batch_set_offset" }, args: A("offset_mm", 2500)));
            d.Add(new("find elements within 500mm radius", PromptIntent.Query, null, new[] { "find_elements_near" }, args: A("radius_mm", 500)));
            d.Add(new("tìm phần tử trong bán kính 500mm", PromptIntent.Query, null, new[] { "find_elements_near" }, args: A("radius_mm", 500)));
            d.Add(new("auto size pipe at 1.5 m/s", PromptIntent.Modify, "Pipes", new[] { "auto_size_mep" }, args: A("target_velocity_ms", 1.5)));
            d.Add(new("tự động size ống nước 1.5 m/s", PromptIntent.Modify, "Pipes", new[] { "auto_size_mep" }, args: A("target_velocity_ms", 1.5)));
            d.Add(new("slope 1.5% for sanitary pipes", PromptIntent.Modify, "Pipes", new[] { "set_pipe_slope" }, args: A("slope_pct", 1.5), system: "Sanitary"));
            d.Add(new("độ dốc 1.5% ống thoát nước", PromptIntent.Modify, "Pipes", new[] { "set_pipe_slope" }, args: A("slope_pct", 1.5), system: "Sanitary"));
            d.Add(new("split ducts every 3m", PromptIntent.Modify, "Ducts", new[] { "split_mep_elements" }, args: A("segment_length_mm", 3000)));
            d.Add(new("chia ống gió mỗi 3m", PromptIntent.Modify, "Ducts", new[] { "split_mep_elements" }, args: A("segment_length_mm", 3000)));
        }

        // ── DRY RUN ──────────────────────────────────────────────
        private static void AddDryRun(List<TrainingSample> d)
        {
            d.Add(new("preview delete all unused elements", PromptIntent.Delete, null, new[] { "delete_elements" }));
            d.Add(new("xem trước xóa phần tử không dùng", PromptIntent.Delete, null, new[] { "delete_elements" }));
            d.Add(new("simulate resize ducts to 400mm", PromptIntent.Modify, "Ducts", new[] { "resize_mep_elements" }, args: A("diameter_mm", 400)));
            d.Add(new("mô phỏng đổi size ống gió 400mm", PromptIntent.Modify, "Ducts", new[] { "resize_mep_elements" }, args: A("diameter_mm", 400)));
            d.Add(new("just show what would change if I set slope 2%", PromptIntent.Modify, "Pipes", new[] { "set_pipe_slope" }, args: A("slope_pct", 2.0)));
            d.Add(new("chỉ xem thay đổi nếu đặt dốc 2%", PromptIntent.Modify, "Pipes", new[] { "set_pipe_slope" }, args: A("slope_pct", 2.0)));
            d.Add(new("preview auto size at 3 m/s", PromptIntent.Modify, null, new[] { "auto_size_mep" }, args: A("target_velocity_ms", 3.0)));
            d.Add(new("thử trước auto size 3 m/s", PromptIntent.Modify, null, new[] { "auto_size_mep" }, args: A("target_velocity_ms", 3.0)));
            d.Add(new("dry run rename pipes", PromptIntent.Modify, "Pipes", new[] { "rename_elements" }));
            d.Add(new("xem trước đổi tên ống nước", PromptIntent.Modify, "Pipes", new[] { "rename_elements" }));
        }

        // ── LIMIT / TOP N ────────────────────────────────────────
        private static void AddLimitTopN(List<TrainingSample> d)
        {
            d.Add(new("show top 10 largest ducts", PromptIntent.Query, "Ducts", new[] { "get_elements" }));
            d.Add(new("hiển thị top 10 ống gió lớn nhất", PromptIntent.Query, "Ducts", new[] { "get_elements" }));
            d.Add(new("first 20 pipes on level 1", PromptIntent.Query, "Pipes", new[] { "get_elements" }));
            d.Add(new("lấy 20 ống nước đầu tiên tầng 1", PromptIntent.Query, "Pipes", new[] { "get_elements" }));
            d.Add(new("limit 50 elements", PromptIntent.Query, null, new[] { "get_elements" }));
            d.Add(new("tối đa 50 phần tử", PromptIntent.Query, null, new[] { "get_elements" }));
            d.Add(new("top 5 warnings", PromptIntent.Check, null, new[] { "get_model_warnings" }));
            d.Add(new("5 cảnh báo đầu tiên", PromptIntent.Check, null, new[] { "get_model_warnings" }));
        }

        // ── DIRECTIONAL ──────────────────────────────────────────
        private static void AddDirectional(List<TrainingSample> d)
        {
            d.Add(new("move selected to the right 300mm", PromptIntent.Modify, null, new[] { "move_elements" }));
            d.Add(new("di chuyển phần tử sang phải 300mm", PromptIntent.Modify, null, new[] { "move_elements" }));
            d.Add(new("move up 500mm", PromptIntent.Modify, null, new[] { "move_elements" }));
            d.Add(new("dịch lên trên 500mm", PromptIntent.Modify, null, new[] { "move_elements" }));
            d.Add(new("shift ducts to the left 200mm", PromptIntent.Modify, "Ducts", new[] { "move_elements" }));
            d.Add(new("dịch ống gió sang trái 200mm", PromptIntent.Modify, "Ducts", new[] { "move_elements" }));
            d.Add(new("move pipe down 1 foot", PromptIntent.Modify, "Pipes", new[] { "move_elements" }));
            d.Add(new("hạ ống nước xuống 1 foot", PromptIntent.Modify, "Pipes", new[] { "move_elements" }));
        }

        // ── AMBIGUOUS ────────────────────────────────────────────
        private static void AddAmbiguous(List<TrainingSample> d)
        {
            d.Add(new("kiểm tra ống hệ cấp gió", PromptIntent.Check, "Ducts", new[] { "check_velocity" }, system: "Supply Air"));
            d.Add(new("liệt kê ống hệ nước lạnh", PromptIntent.Query, "Pipes", new[] { "get_elements" }, system: "Chilled Water"));
            d.Add(new("đếm ống hệ thoát nước", PromptIntent.Count, "Pipes", new[] { "count_elements" }, system: "Sanitary"));
            d.Add(new("count pipes in domestic water", PromptIntent.Count, "Pipes", new[] { "count_elements" }, system: "Domestic Water"));
            d.Add(new("ống hệ hồi gió bao nhiêu", PromptIntent.Count, "Ducts", new[] { "count_elements" }, system: "Return Air"));
            d.Add(new("check ống fire protection", PromptIntent.Check, "Pipes", new[] { "check_disconnected_elements" }, system: "Fire Protection"));
            d.Add(new("list ống exhaust", PromptIntent.Query, "Ducts", new[] { "get_elements" }, system: "Exhaust Air"));
            d.Add(new("liệt kê ống condensate", PromptIntent.Query, "Pipes", new[] { "get_elements" }, system: "Condensate"));
        }

        // ── SYSTEM ABBREVIATIONS ─────────────────────────────────
        private static void AddSystemAbbr(List<TrainingSample> d)
        {
            d.Add(new("show CHW pipes level 1", PromptIntent.Query, "Pipes", new[] { "get_elements" }, system: "Chilled Water"));
            d.Add(new("hiển thị ống CHW tầng 1", PromptIntent.Query, "Pipes", new[] { "get_elements" }, system: "Chilled Water"));
            d.Add(new("list SA ducts", PromptIntent.Query, "Ducts", new[] { "get_elements" }, system: "Supply Air"));
            d.Add(new("liệt kê ống SA", PromptIntent.Query, "Ducts", new[] { "get_elements" }, system: "Supply Air"));
            d.Add(new("count EA ducts on level 2", PromptIntent.Count, "Ducts", new[] { "count_elements" }, system: "Exhaust Air"));
            d.Add(new("đếm ống EA tầng 2", PromptIntent.Count, "Ducts", new[] { "count_elements" }, system: "Exhaust Air"));
            d.Add(new("check HW pipes", PromptIntent.Check, "Pipes", new[] { "check_disconnected_elements" }, system: "Hot Water"));
            d.Add(new("kiểm tra ống HW", PromptIntent.Check, "Pipes", new[] { "check_disconnected_elements" }, system: "Hot Water"));
            d.Add(new("summary FP system", PromptIntent.Analyze, null, new[] { "calculate_system_totals" }, system: "Fire Protection"));
            d.Add(new("thống kê hệ PCCC", PromptIntent.Analyze, null, new[] { "calculate_system_totals" }, system: "Fire Protection"));
        }

        // ── DN / DIMENSION NOTATION ──────────────────────────────
        private static void AddDnDimension(List<TrainingSample> d)
        {
            d.Add(new("find pipes DN100", PromptIntent.Query, "Pipes", new[] { "get_elements" }));
            d.Add(new("tìm ống DN100", PromptIntent.Query, "Pipes", new[] { "get_elements" }));
            d.Add(new("resize to DN150", PromptIntent.Modify, "Pipes", new[] { "resize_mep_elements" }));
            d.Add(new("đổi sang DN150", PromptIntent.Modify, "Pipes", new[] { "resize_mep_elements" }));
            d.Add(new("count ducts and pipes on level 1", PromptIntent.Count, "Ducts", new[] { "count_elements" }));
            d.Add(new("đếm ống gió và ống nước tầng 1", PromptIntent.Count, "Ducts", new[] { "count_elements" }));
            d.Add(new("find duct ø300", PromptIntent.Query, "Ducts", new[] { "get_elements" }));
            d.Add(new("tìm ống gió ø300", PromptIntent.Query, "Ducts", new[] { "get_elements" }));
            d.Add(new("resize duct to 600x400mm", PromptIntent.Modify, "Ducts", new[] { "resize_mep_elements" }, args: A2("width_mm", 600, "height_mm", 400)));
            d.Add(new("đổi ống gió sang 600x400mm", PromptIntent.Modify, "Ducts", new[] { "resize_mep_elements" }, args: A2("width_mm", 600, "height_mm", 400)));
        }

        // ── CONVERSATIONAL / FOLLOW-UP ───────────────────────────
        private static void AddConversational(List<TrainingSample> d)
        {
            d.Add(new("do the same for pipes", PromptIntent.Query, "Pipes", new[] { "get_elements" }));
            d.Add(new("tương tự cho ống nước", PromptIntent.Query, "Pipes", new[] { "get_elements" }));
            d.Add(new("same thing on level 2", PromptIntent.Query, null, new[] { "get_elements" }));
            d.Add(new("như trên nhưng tầng 2", PromptIntent.Query, null, new[] { "get_elements" }));
            d.Add(new("now check the velocity", PromptIntent.Check, null, new[] { "check_velocity" }));
            d.Add(new("giờ kiểm tra vận tốc", PromptIntent.Check, null, new[] { "check_velocity" }));
            d.Add(new("export that to CSV", PromptIntent.Export, null, new[] { "export_to_csv" }));
            d.Add(new("xuất cái đó ra CSV", PromptIntent.Export, null, new[] { "export_to_csv" }));
            d.Add(new("also for Return Air system", PromptIntent.Query, null, new[] { "get_elements" }, system: "Return Air"));
            d.Add(new("làm lại với hệ hồi gió", PromptIntent.Query, null, new[] { "get_elements" }, system: "Return Air"));
        }

        #endregion

        // ═════════════════════════════════════════════════════════
        //  CONVERSATION CHAINS — 24 chains × 12-15 steps each
        //  covering all probable intent/category/system/level
        //  transitions to train ConversationContextTracker
        // ═════════════════════════════════════════════════════════
        #region Conversation Chains

        private static TrainingSample CS(string prompt, PromptIntent intent,
            string category = null, string[] tools = null,
            string system = null, Dictionary<string, object> args = null)
            => new(prompt, intent, category, tools ?? Array.Empty<string>(), args, system);

        private static List<ConversationChain> BuildConversationChains()
        {
            return new List<ConversationChain>
            {
                Chain01_DuctWorkflowEN(),
                Chain02_PipeWorkflowVN(),
                Chain03_MultiSystemComparison(),
                Chain04_QAAuditMixed(),
                Chain05_CategoryHopping(),
                Chain06_LevelTransitions(),
                Chain07_SystemAbbreviations(),
                Chain08_CreateModifyCheck(),
                Chain09_VisualExplorationVN(),
                Chain10_ElectricalWorkflowVN(),
                Chain11_StructuralMEPCrossover(),
                Chain12_ConnectRouteWorkflow(),
                Chain13_TagAnnotateWorkflow(),
                Chain14_DeleteCleanup(),
                Chain15_NumericParameterChain(),
                Chain16_MixedLanguageSession(),
                Chain17_AmbiguousResolution(),
                Chain18_ExportMultiFormat(),
                Chain19_NavigateSelectWorkflow(),
                Chain20_DryRunExecuteVN(),
                Chain21_RoomSpaceWorkflow(),
                Chain22_ProjectReview(),
                Chain23_PlumbingWorkflow(),
                Chain24_FireProtectionWorkflow(),
            };
        }

        // ── Chain 01: Duct Full Workflow (EN) ─────────────────────
        // Intent: Query→Count→Analyze→Check→Check→Modify→DryRun→Modify→Check→Visual→Export→Export(switch)→Count(switch)
        private static ConversationChain Chain01_DuctWorkflowEN() => new("Duct Full Workflow EN", new()
        {
            CS("list all ducts on level 1", PromptIntent.Query, "Ducts", new[]{"get_elements","get_duct_summary"}),
            CS("how many are there", PromptIntent.Count, "Ducts", new[]{"count_elements"}),
            CS("summarize by system", PromptIntent.Analyze, "Ducts", new[]{"get_duct_summary","calculate_system_totals"}),
            CS("check velocity", PromptIntent.Check, "Ducts", new[]{"check_velocity"}),
            CS("which ones exceed 5 m/s", PromptIntent.Check, "Ducts", new[]{"check_velocity"}, args: A("max_velocity_ms", 5.0)),
            CS("resize those to 400mm", PromptIntent.Modify, "Ducts", new[]{"resize_mep_elements"}, args: A("diameter_mm", 400)),
            CS("preview first", PromptIntent.Modify, "Ducts", new[]{"resize_mep_elements"}),
            CS("ok apply the changes", PromptIntent.Modify, "Ducts", new[]{"resize_mep_elements"}),
            CS("now check velocity again", PromptIntent.Check, "Ducts", new[]{"check_velocity"}),
            CS("color them by system", PromptIntent.Visual, "Ducts", new[]{"override_color_by_system"}),
            CS("export to CSV", PromptIntent.Export, "Ducts", new[]{"export_to_csv","export_mep_boq"}),
            CS("do the same for pipes", PromptIntent.Export, "Pipes", new[]{"export_to_csv","export_mep_boq"}),
            CS("how many pipes total", PromptIntent.Count, "Pipes", new[]{"count_elements"}),
        });

        // ── Chain 02: Pipe Full Workflow (VN) ─────────────────────
        // Intent: Query→Count→Analyze→Check→Modify→DryRun→Modify→Check→Visual→Export→Query(switch)→Count(switch)→Analyze(switch)
        private static ConversationChain Chain02_PipeWorkflowVN() => new("Pipe Full Workflow VN", new()
        {
            CS("liệt kê ống nước tầng 1", PromptIntent.Query, "Pipes", new[]{"get_elements"}),
            CS("đếm bao nhiêu", PromptIntent.Count, "Pipes", new[]{"count_elements"}),
            CS("thống kê theo hệ thống", PromptIntent.Analyze, "Pipes", new[]{"get_pipe_summary","calculate_system_totals"}),
            CS("kiểm tra độ dốc", PromptIntent.Check, "Pipes", new[]{"check_pipe_slope"}),
            CS("đặt độ dốc 2%", PromptIntent.Modify, "Pipes", new[]{"set_pipe_slope"}, args: A("slope_pct", 2.0)),
            CS("xem trước thay đổi", PromptIntent.Modify, "Pipes", new[]{"set_pipe_slope"}),
            CS("thực hiện đi", PromptIntent.Modify, "Pipes", new[]{"set_pipe_slope"}),
            CS("kiểm tra lại", PromptIntent.Check, "Pipes", new[]{"check_pipe_slope"}),
            CS("tô màu theo hệ thống", PromptIntent.Visual, "Pipes", new[]{"override_color_by_system"}),
            CS("xuất ra CSV", PromptIntent.Export, "Pipes", new[]{"export_to_csv"}),
            CS("tương tự cho ống gió", PromptIntent.Query, "Ducts", new[]{"get_elements"}),
            CS("bao nhiêu ống gió", PromptIntent.Count, "Ducts", new[]{"count_elements"}),
            CS("thống kê ống gió", PromptIntent.Analyze, "Ducts", new[]{"get_duct_summary"}),
        });

        // ── Chain 03: Multi-System Comparison (EN) ────────────────
        // System transitions: SA→EA→RA + Count carryover
        private static ConversationChain Chain03_MultiSystemComparison() => new("Multi-System Comparison EN", new()
        {
            CS("list Supply Air ducts", PromptIntent.Query, "Ducts", new[]{"get_elements"}, system: "Supply Air"),
            CS("count them", PromptIntent.Count, "Ducts", new[]{"count_elements"}),
            CS("now show Exhaust Air ducts", PromptIntent.Query, "Ducts", new[]{"get_elements"}, system: "Exhaust Air"),
            CS("how many", PromptIntent.Count, "Ducts", new[]{"count_elements"}),
            CS("show Return Air ducts too", PromptIntent.Query, "Ducts", new[]{"get_elements"}, system: "Return Air"),
            CS("count those too", PromptIntent.Count, "Ducts", new[]{"count_elements"}),
            CS("analyze Supply Air totals", PromptIntent.Analyze, null, new[]{"calculate_system_totals"}, system: "Supply Air"),
            CS("same for Exhaust Air", PromptIntent.Analyze, null, new[]{"calculate_system_totals"}, system: "Exhaust Air"),
            CS("and Return Air too", PromptIntent.Analyze, null, new[]{"calculate_system_totals"}, system: "Return Air"),
            CS("check velocity for all ducts", PromptIntent.Check, "Ducts", new[]{"check_velocity"}),
            CS("which system has issues", PromptIntent.Analyze, "Ducts", new[]{"calculate_system_totals"}),
            CS("color all ducts by system", PromptIntent.Visual, "Ducts", new[]{"override_color_by_system"}),
            CS("export comparison to CSV", PromptIntent.Export, "Ducts", new[]{"export_to_csv"}),
        });

        // ── Chain 04: QA Audit (Mixed EN/VN) ──────────────────────
        // Multiple check types + fix + verify pattern
        private static ConversationChain Chain04_QAAuditMixed() => new("QA Audit Mixed", new()
        {
            CS("check model health", PromptIntent.Check, null, new[]{"audit_model_standards"}),
            CS("kiểm tra va chạm", PromptIntent.Check, null, new[]{"check_clashes","get_clash_summary"}),
            CS("check disconnected elements", PromptIntent.Check, null, new[]{"check_disconnected_elements"}),
            CS("kiểm tra bảo ôn", PromptIntent.Check, null, new[]{"check_insulation_coverage"}),
            CS("how many warnings total", PromptIntent.Count, null, new[]{"count_elements"}),
            CS("top 10 warnings", PromptIntent.Check, null, new[]{"get_model_warnings"}),
            CS("check velocity for ducts", PromptIntent.Check, "Ducts", new[]{"check_velocity"}),
            CS("kiểm tra độ dốc ống nước", PromptIntent.Check, "Pipes", new[]{"check_pipe_slope"}),
            CS("fix the disconnected ducts", PromptIntent.Modify, "Ducts", new[]{"connect_mep_elements"}),
            CS("kiểm tra lại kết nối", PromptIntent.Check, null, new[]{"check_disconnected_elements"}),
            CS("color problematic elements red", PromptIntent.Visual, null, new[]{"override_category_color"}),
            CS("export audit report to CSV", PromptIntent.Export, null, new[]{"export_to_csv"}),
            CS("chụp ảnh view hiện tại", PromptIntent.Visual, null, new[]{"screenshot_view"}),
        });

        // ── Chain 05: Category Hopping ────────────────────────────
        // Transitions: Ducts→Pipes→CableTrays→Conduits→back
        private static ConversationChain Chain05_CategoryHopping() => new("Category Hopping", new()
        {
            CS("list all ducts on level 1", PromptIntent.Query, "Ducts", new[]{"get_elements"}),
            CS("same for pipes", PromptIntent.Query, "Pipes", new[]{"get_elements"}),
            CS("and cable trays", PromptIntent.Query, "Cable Trays", new[]{"get_elements"}),
            CS("also conduits", PromptIntent.Query, "Conduits", new[]{"get_elements"}),
            CS("count ducts", PromptIntent.Count, "Ducts", new[]{"count_elements"}),
            CS("how many pipes", PromptIntent.Count, "Pipes", new[]{"count_elements"}),
            CS("cable tray count", PromptIntent.Count, "Cable Trays", new[]{"count_elements"}),
            CS("conduit count", PromptIntent.Count, "Conduits", new[]{"count_elements"}),
            CS("analyze duct summary", PromptIntent.Analyze, "Ducts", new[]{"get_duct_summary"}),
            CS("tương tự cho ống nước", PromptIntent.Analyze, "Pipes", new[]{"get_pipe_summary"}),
            CS("check velocity for ducts", PromptIntent.Check, "Ducts", new[]{"check_velocity"}),
            CS("check slope for pipes", PromptIntent.Check, "Pipes", new[]{"check_pipe_slope"}),
            CS("export all to CSV", PromptIntent.Export, null, new[]{"export_to_csv"}),
        });

        // ── Chain 06: Level Transitions ───────────────────────────
        // Level: L1→L2→L3→back to L1(pipes)→L2
        private static ConversationChain Chain06_LevelTransitions() => new("Level Transitions", new()
        {
            CS("list all ducts on level 1", PromptIntent.Query, "Ducts", new[]{"get_elements"}),
            CS("count them", PromptIntent.Count, "Ducts", new[]{"count_elements"}),
            CS("check velocity", PromptIntent.Check, "Ducts", new[]{"check_velocity"}),
            CS("same thing on level 2", PromptIntent.Query, "Ducts", new[]{"get_elements"}),
            CS("count those", PromptIntent.Count, "Ducts", new[]{"count_elements"}),
            CS("check velocity like above", PromptIntent.Check, "Ducts", new[]{"check_velocity"}),
            CS("now level 3 ducts", PromptIntent.Query, "Ducts", new[]{"get_elements"}),
            CS("count again", PromptIntent.Count, "Ducts", new[]{"count_elements"}),
            CS("check velocity", PromptIntent.Check, "Ducts", new[]{"check_velocity"}),
            CS("back to level 1 but pipes", PromptIntent.Query, "Pipes", new[]{"get_elements"}),
            CS("count them", PromptIntent.Count, "Pipes", new[]{"count_elements"}),
            CS("export level 1 data", PromptIntent.Export, null, new[]{"export_to_csv"}),
            CS("như trên nhưng tầng 2", PromptIntent.Export, null, new[]{"export_to_csv"}),
        });

        // ── Chain 07: System Abbreviations ────────────────────────
        // System: CHW→HW→SA→EA→RA→FP
        private static ConversationChain Chain07_SystemAbbreviations() => new("System Abbreviation Chain", new()
        {
            CS("show CHW pipes", PromptIntent.Query, "Pipes", new[]{"get_elements"}, system: "Chilled Water"),
            CS("count them", PromptIntent.Count, "Pipes", new[]{"count_elements"}),
            CS("now HW pipes", PromptIntent.Query, "Pipes", new[]{"get_elements"}, system: "Hot Water"),
            CS("how many", PromptIntent.Count, "Pipes", new[]{"count_elements"}),
            CS("switch to SA ducts", PromptIntent.Query, "Ducts", new[]{"get_elements"}, system: "Supply Air"),
            CS("count those", PromptIntent.Count, "Ducts", new[]{"count_elements"}),
            CS("show EA ducts", PromptIntent.Query, "Ducts", new[]{"get_elements"}, system: "Exhaust Air"),
            CS("bao nhiêu", PromptIntent.Count, "Ducts", new[]{"count_elements"}),
            CS("check RA ducts velocity", PromptIntent.Check, "Ducts", new[]{"check_velocity"}, system: "Return Air"),
            CS("check FP pipes", PromptIntent.Check, "Pipes", new[]{"check_disconnected_elements"}, system: "Fire Protection"),
            CS("summary all systems", PromptIntent.Analyze, null, new[]{"calculate_system_totals"}),
            CS("export to CSV", PromptIntent.Export, null, new[]{"export_to_csv"}),
            CS("also to IFC", PromptIntent.Export, null, new[]{"export_ifc"}),
        });

        // ── Chain 08: Create-Modify-Check Loop ────────────────────
        // Intent: Create→Create→Create→Modify→Modify→Check→Tag→Tag→Visual→Visual→Visual→Export→Visual
        private static ConversationChain Chain08_CreateModifyCheck() => new("Create-Modify-Check Loop", new()
        {
            CS("create a wall on level 1", PromptIntent.Create, "Walls", new[]{"create_element"}),
            CS("add a door to it", PromptIntent.Create, "Doors", new[]{"create_element"}),
            CS("add a window too", PromptIntent.Create, "Windows", new[]{"create_element"}),
            CS("move the door 500mm to the right", PromptIntent.Modify, "Doors", new[]{"move_elements"}),
            CS("resize window to 1200mm", PromptIntent.Modify, "Windows", new[]{"resize_mep_elements"}),
            CS("check if elements are connected", PromptIntent.Check, null, new[]{"check_disconnected_elements"}),
            CS("tag the door", PromptIntent.Tag, "Doors", new[]{"tag_all_in_view"}),
            CS("tag the window too", PromptIntent.Tag, "Windows", new[]{"tag_all_in_view"}),
            CS("color walls by type", PromptIntent.Visual, "Walls", new[]{"override_color_by_parameter"}),
            CS("hide ceiling", PromptIntent.Visual, "Ceilings", new[]{"hide_category"}),
            CS("show everything", PromptIntent.Visual, null, new[]{"reset_view_isolation"}),
            CS("export the floor plan to PDF", PromptIntent.Export, null, new[]{"export_pdf"}),
            CS("take a screenshot", PromptIntent.Visual, null, new[]{"screenshot_view"}),
        });

        // ── Chain 09: Visual Exploration (VN) ─────────────────────
        // Full visual workflow with category switches
        private static ConversationChain Chain09_VisualExplorationVN() => new("Visual Exploration VN", new()
        {
            CS("tô màu ống gió theo hệ thống", PromptIntent.Visual, "Ducts", new[]{"override_color_by_system"}),
            CS("cô lập ống nước tầng 1", PromptIntent.Visual, "Pipes", new[]{"isolate_category"}),
            CS("ẩn tường", PromptIntent.Visual, "Walls", new[]{"hide_category"}),
            CS("ẩn trần", PromptIntent.Visual, "Ceilings", new[]{"hide_category"}),
            CS("hiện tất cả phần tử", PromptIntent.Visual, null, new[]{"reset_view_isolation"}),
            CS("tạo view 3D cho hệ cấp gió", PromptIntent.Visual, null, new[]{"create_3d_view_by_system"}, system: "Supply Air"),
            CS("chụp ảnh view", PromptIntent.Visual, null, new[]{"screenshot_view"}),
            CS("tô màu ống nước đỏ", PromptIntent.Visual, "Pipes", new[]{"override_category_color"}),
            CS("đặt trong suốt 50% cho ống gió", PromptIntent.Visual, "Ducts", new[]{"override_category_color"}, args: A("transparency", 50)),
            CS("cô lập tầng 1", PromptIntent.Visual, null, new[]{"isolate_by_level"}),
            CS("so sánh hai view", PromptIntent.Visual, null, new[]{"compare_views"}),
            CS("xuất PDF", PromptIntent.Export, null, new[]{"export_pdf"}),
            CS("chụp thêm 1 ảnh", PromptIntent.Visual, null, new[]{"screenshot_view"}),
        });

        // ── Chain 10: Electrical Full Workflow (VN) ───────────────
        // Electrical→Lighting→Conduits→CableTrays
        private static ConversationChain Chain10_ElectricalWorkflowVN() => new("Electrical Workflow VN", new()
        {
            CS("hiển thị bảng panel", PromptIntent.Query, "Electrical Equipment", new[]{"get_panel_schedules"}),
            CS("kiểm tra dung lượng tủ điện", PromptIntent.Check, "Electrical Equipment", new[]{"check_panel_capacity"}),
            CS("lấy tải mạch", PromptIntent.Query, "Electrical Equipment", new[]{"get_circuit_loads"}),
            CS("kiểm tra sụt áp", PromptIntent.Check, "Electrical Equipment", new[]{"get_voltage_drop"}),
            CS("phân tích cân bằng pha", PromptIntent.Analyze, "Electrical Equipment", new[]{"get_phase_balance"}),
            CS("liệt kê đèn tầng 1", PromptIntent.Query, "Lighting Fixtures", new[]{"get_elements"}),
            CS("đếm bao nhiêu", PromptIntent.Count, "Lighting Fixtures", new[]{"count_elements"}),
            CS("hiển thị ống dẫn tầng 2", PromptIntent.Query, "Conduits", new[]{"get_elements"}),
            CS("đếm ống dẫn", PromptIntent.Count, "Conduits", new[]{"count_elements"}),
            CS("khay cáp tầng 2", PromptIntent.Query, "Cable Trays", new[]{"get_elements"}),
            CS("đếm khay cáp", PromptIntent.Count, "Cable Trays", new[]{"count_elements"}),
            CS("xuất tất cả ra CSV", PromptIntent.Export, null, new[]{"export_to_csv"}),
            CS("kiểm tra kết nối", PromptIntent.Check, null, new[]{"check_disconnected_elements"}),
        });

        // ── Chain 11: Structural-MEP Crossover (Mixed) ────────────
        // Structural→Ducts→Columns→Beams→Check clashes→Fix
        private static ConversationChain Chain11_StructuralMEPCrossover() => new("Structural-MEP Crossover", new()
        {
            CS("show structural model", PromptIntent.Query, null, new[]{"get_structural_model"}),
            CS("check rebar coverage", PromptIntent.Check, null, new[]{"check_rebar_coverage"}),
            CS("bảng thống kê thép tầng 1", PromptIntent.Query, null, new[]{"get_rebar_schedule"}),
            CS("kiểm tra tải móng", PromptIntent.Check, null, new[]{"check_foundation_loads"}),
            CS("list ducts on level 1", PromptIntent.Query, "Ducts", new[]{"get_elements"}),
            CS("run clash detection", PromptIntent.Check, null, new[]{"check_clashes","get_clash_summary"}),
            CS("list columns level 1", PromptIntent.Query, "Columns", new[]{"get_elements"}),
            CS("show beams level 1", PromptIntent.Query, "Structural Framing", new[]{"get_elements"}),
            CS("check ducts near beams", PromptIntent.Check, "Ducts", new[]{"check_clashes"}),
            CS("move ducts down 200mm", PromptIntent.Modify, "Ducts", new[]{"move_elements"}),
            CS("verify no more clashes", PromptIntent.Check, null, new[]{"check_clashes"}),
            CS("export clash report to CSV", PromptIntent.Export, null, new[]{"export_to_csv"}),
            CS("chụp ảnh view", PromptIntent.Visual, null, new[]{"screenshot_view"}),
        });

        // ── Chain 12: Connect & Route Workflow ────────────────────
        // Connect→Check→Connect(switch)→Check→Route→Check→Connect→Verify→Tag→Visual→Export
        private static ConversationChain Chain12_ConnectRouteWorkflow() => new("Connect Route Workflow", new()
        {
            CS("select 2 ducts", PromptIntent.Navigate, "Ducts", new[]{"select_elements"}),
            CS("connect them", PromptIntent.Connect, "Ducts", new[]{"connect_mep_elements"}),
            CS("check connection", PromptIntent.Check, "Ducts", new[]{"check_disconnected_elements"}),
            CS("nối 2 ống nước đang chọn", PromptIntent.Connect, "Pipes", new[]{"connect_mep_elements"}),
            CS("kiểm tra kết nối", PromptIntent.Check, "Pipes", new[]{"check_disconnected_elements"}),
            CS("route duct from FCU to main", PromptIntent.Connect, "Ducts", new[]{"route_mep_between"}),
            CS("avoid structural elements", PromptIntent.Connect, "Ducts", new[]{"route_mep_between"}),
            CS("check velocity after routing", PromptIntent.Check, "Ducts", new[]{"check_velocity"}),
            CS("connect pipe to equipment", PromptIntent.Connect, "Pipes", new[]{"connect_mep_elements"}),
            CS("verify connections", PromptIntent.Check, "Pipes", new[]{"check_disconnected_elements"}),
            CS("tag new connections", PromptIntent.Tag, "Pipes", new[]{"tag_all_in_view"}),
            CS("color by system", PromptIntent.Visual, "Pipes", new[]{"override_color_by_system"}),
            CS("export to CSV", PromptIntent.Export, "Pipes", new[]{"export_to_csv"}),
        });

        // ── Chain 13: Tag & Annotate Workflow ─────────────────────
        // Tag multiple categories + verify + export
        private static ConversationChain Chain13_TagAnnotateWorkflow() => new("Tag Annotate Workflow", new()
        {
            CS("find untagged elements", PromptIntent.Tag, null, new[]{"get_untagged_elements"}),
            CS("tag all untagged ducts", PromptIntent.Tag, "Ducts", new[]{"get_untagged_elements","tag_all_in_view"}),
            CS("tìm ống nước chưa tag", PromptIntent.Tag, "Pipes", new[]{"get_untagged_elements"}),
            CS("tag tất cả ống nước", PromptIntent.Tag, "Pipes", new[]{"tag_all_in_view"}),
            CS("find untagged equipment level 1", PromptIntent.Tag, "Mechanical Equipment", new[]{"get_untagged_elements"}),
            CS("tag those", PromptIntent.Tag, "Mechanical Equipment", new[]{"tag_all_in_view"}),
            CS("check if all ducts are tagged", PromptIntent.Tag, "Ducts", new[]{"get_untagged_elements"}),
            CS("tương tự cho ống nước", PromptIntent.Tag, "Pipes", new[]{"get_untagged_elements"}),
            CS("annotate level 2 elements", PromptIntent.Tag, null, new[]{"get_untagged_elements","tag_all_in_view"}),
            CS("count tagged elements", PromptIntent.Count, null, new[]{"count_elements"}),
            CS("count untagged remaining", PromptIntent.Count, null, new[]{"count_elements"}),
            CS("tag remaining", PromptIntent.Tag, null, new[]{"tag_all_in_view"}),
            CS("export tagged data to CSV", PromptIntent.Export, null, new[]{"export_to_csv"}),
        });

        // ── Chain 14: Delete & Cleanup ────────────────────────────
        // Find→Preview→Delete→Verify cycle
        private static ConversationChain Chain14_DeleteCleanup() => new("Delete Cleanup", new()
        {
            CS("find unused families", PromptIntent.Query, null, new[]{"get_elements"}),
            CS("how many unused", PromptIntent.Count, null, new[]{"count_elements"}),
            CS("preview delete unused elements", PromptIntent.Delete, null, new[]{"delete_elements"}),
            CS("go ahead and delete", PromptIntent.Delete, null, new[]{"delete_elements"}),
            CS("delete unused views", PromptIntent.Delete, null, new[]{"delete_elements"}),
            CS("xem trước xóa phụ kiện ống gió tầng 1", PromptIntent.Delete, "Duct Accessories", new[]{"delete_elements"}),
            CS("thực hiện xóa", PromptIntent.Delete, null, new[]{"delete_elements"}),
            CS("kiểm tra model", PromptIntent.Check, null, new[]{"audit_model_standards"}),
            CS("purge remaining unused", PromptIntent.Delete, null, new[]{"delete_elements"}),
            CS("verify cleanup", PromptIntent.Check, null, new[]{"audit_model_standards"}),
            CS("count remaining warnings", PromptIntent.Count, null, new[]{"count_elements"}),
            CS("audit model standards", PromptIntent.Check, null, new[]{"audit_model_standards"}),
            CS("export final report", PromptIntent.Export, null, new[]{"export_to_csv"}),
        });

        // ── Chain 15: Numeric Parameter Intensive ─────────────────
        // Multiple resize→check→adjust cycles with numeric params
        private static ConversationChain Chain15_NumericParameterChain() => new("Numeric Parameter Chain", new()
        {
            CS("resize ducts to 300mm", PromptIntent.Modify, "Ducts", new[]{"resize_mep_elements"}, args: A("diameter_mm", 300)),
            CS("check velocity at that size", PromptIntent.Check, "Ducts", new[]{"check_velocity"}),
            CS("try 400mm instead", PromptIntent.Modify, "Ducts", new[]{"resize_mep_elements"}, args: A("diameter_mm", 400)),
            CS("check velocity now", PromptIntent.Check, "Ducts", new[]{"check_velocity"}),
            CS("auto size at 4 m/s", PromptIntent.Modify, "Ducts", new[]{"auto_size_mep"}, args: A("target_velocity_ms", 4.0)),
            CS("set slope 2% for pipes", PromptIntent.Modify, "Pipes", new[]{"set_pipe_slope"}, args: A("slope_pct", 2.0)),
            CS("check slope", PromptIntent.Check, "Pipes", new[]{"check_pipe_slope"}),
            CS("add insulation 25mm", PromptIntent.Modify, "Pipes", new[]{"add_change_insulation"}, args: A("thickness_mm", 25)),
            CS("split pipes every 1350mm", PromptIntent.Modify, "Pipes", new[]{"split_mep_elements"}, args: A("segment_length_mm", 1350)),
            CS("offset 2500mm", PromptIntent.Modify, "Pipes", new[]{"batch_set_offset"}, args: A("offset_mm", 2500)),
            CS("kiểm tra lại", PromptIntent.Check, "Pipes", new[]{"check_pipe_slope"}),
            CS("xuất thống kê", PromptIntent.Export, "Pipes", new[]{"export_to_csv"}),
            CS("tương tự cho ống gió", PromptIntent.Export, "Ducts", new[]{"export_to_csv"}),
        });

        // ── Chain 16: Mixed Language Session ──────────────────────
        // Alternating EN/VN throughout the conversation
        private static ConversationChain Chain16_MixedLanguageSession() => new("Mixed Language Session", new()
        {
            CS("list all ducts on level 1", PromptIntent.Query, "Ducts", new[]{"get_elements"}),
            CS("đếm bao nhiêu", PromptIntent.Count, "Ducts", new[]{"count_elements"}),
            CS("thống kê theo hệ thống", PromptIntent.Analyze, "Ducts", new[]{"get_duct_summary","calculate_system_totals"}),
            CS("check velocity", PromptIntent.Check, "Ducts", new[]{"check_velocity"}),
            CS("vận tốc tối đa 5 m/s", PromptIntent.Check, null, new[]{"check_velocity"}, args: A("max_velocity_ms", 5.0)),
            CS("resize to 400mm", PromptIntent.Modify, "Ducts", new[]{"resize_mep_elements"}, args: A("diameter_mm", 400)),
            CS("xem trước", PromptIntent.Modify, "Ducts", new[]{"resize_mep_elements"}),
            CS("ok do it", PromptIntent.Modify, "Ducts", new[]{"resize_mep_elements"}),
            CS("kiểm tra lại", PromptIntent.Check, "Ducts", new[]{"check_velocity"}),
            CS("color by system", PromptIntent.Visual, "Ducts", new[]{"override_color_by_system"}),
            CS("xuất CSV", PromptIntent.Export, "Ducts", new[]{"export_to_csv"}),
            CS("also export IFC", PromptIntent.Export, null, new[]{"export_ifc"}),
            CS("cảm ơn bạn", PromptIntent.Help, null, Array.Empty<string>()),
        });

        // ── Chain 17: Ambiguous Resolution ────────────────────────
        // Ambiguous "ống" prompts resolved by system context
        private static ConversationChain Chain17_AmbiguousResolution() => new("Ambiguous Resolution", new()
        {
            CS("kiểm tra ống hệ cấp gió", PromptIntent.Check, "Ducts", new[]{"check_velocity"}, system: "Supply Air"),
            CS("count them", PromptIntent.Count, "Ducts", new[]{"count_elements"}),
            CS("liệt kê ống hệ nước lạnh", PromptIntent.Query, "Pipes", new[]{"get_elements"}, system: "Chilled Water"),
            CS("check those pipes", PromptIntent.Check, "Pipes", new[]{"check_disconnected_elements"}),
            CS("now check exhaust air ducts", PromptIntent.Check, "Ducts", new[]{"check_velocity"}, system: "Exhaust Air"),
            CS("how many are disconnected", PromptIntent.Count, "Ducts", new[]{"count_elements"}),
            CS("fix the disconnections", PromptIntent.Modify, "Ducts", new[]{"connect_mep_elements"}),
            CS("verify", PromptIntent.Check, "Ducts", new[]{"check_disconnected_elements"}),
            CS("switch to fire protection pipes", PromptIntent.Query, "Pipes", new[]{"get_elements"}, system: "Fire Protection"),
            CS("check those", PromptIntent.Check, "Pipes", new[]{"check_disconnected_elements"}),
            CS("count FP pipes", PromptIntent.Count, "Pipes", new[]{"count_elements"}),
            CS("export everything to CSV", PromptIntent.Export, null, new[]{"export_to_csv"}),
            CS("summary of all systems", PromptIntent.Analyze, null, new[]{"calculate_system_totals"}),
        });

        // ── Chain 18: Export Multi-Format ─────────────────────────
        // Multiple export formats for different categories
        private static ConversationChain Chain18_ExportMultiFormat() => new("Export Multi-Format", new()
        {
            CS("list ducts on level 1", PromptIntent.Query, "Ducts", new[]{"get_elements"}),
            CS("export to CSV", PromptIntent.Export, "Ducts", new[]{"export_to_csv"}),
            CS("also export to JSON", PromptIntent.Export, "Ducts", new[]{"export_to_json"}),
            CS("export all to IFC", PromptIntent.Export, null, new[]{"export_ifc"}),
            CS("xuất PDF", PromptIntent.Export, null, new[]{"export_pdf"}),
            CS("export gbXML", PromptIntent.Export, null, new[]{"export_gbxml"}),
            CS("list pipes level 1", PromptIntent.Query, "Pipes", new[]{"get_elements"}),
            CS("export those to CSV", PromptIntent.Export, "Pipes", new[]{"export_to_csv"}),
            CS("export equipment data to csv", PromptIntent.Export, "Mechanical Equipment", new[]{"export_to_csv"}),
            CS("save rooms to JSON", PromptIntent.Export, "Rooms", new[]{"export_to_json"}),
            CS("xuất bóc khối lượng ống gió", PromptIntent.Export, "Ducts", new[]{"export_mep_boq"}),
            CS("xuất bóc khối lượng ống nước", PromptIntent.Export, "Pipes", new[]{"export_mep_boq"}),
            CS("export final BOQ", PromptIntent.Export, null, new[]{"export_mep_boq"}),
        });

        // ── Chain 19: Navigate & Select Workflow ──────────────────
        // Select→Zoom→Isolate→Color→Tag→Export
        private static ConversationChain Chain19_NavigateSelectWorkflow() => new("Navigate Select Workflow", new()
        {
            CS("select all ducts", PromptIntent.Navigate, "Ducts", new[]{"select_elements"}),
            CS("zoom to element 123456", PromptIntent.Navigate, null, new[]{"zoom_to_elements"}),
            CS("select pipes on level 1", PromptIntent.Navigate, "Pipes", new[]{"select_elements"}),
            CS("highlight disconnected elements", PromptIntent.Navigate, null, new[]{"select_elements"}),
            CS("go to room 101", PromptIntent.Navigate, "Rooms", new[]{"zoom_to_elements"}),
            CS("select all elements in this room", PromptIntent.Navigate, "Rooms", new[]{"select_elements"}),
            CS("isolate those elements", PromptIntent.Visual, null, new[]{"isolate_category"}),
            CS("color by system", PromptIntent.Visual, null, new[]{"override_color_by_system"}),
            CS("tag untagged ones", PromptIntent.Tag, null, new[]{"get_untagged_elements","tag_all_in_view"}),
            CS("count selected elements", PromptIntent.Count, null, new[]{"count_elements"}),
            CS("export selected to CSV", PromptIntent.Export, null, new[]{"export_to_csv"}),
            CS("screenshot current view", PromptIntent.Visual, null, new[]{"screenshot_view"}),
            CS("reset view", PromptIntent.Visual, null, new[]{"reset_view_isolation"}),
        });

        // ── Chain 20: DryRun-then-Execute (VN) ────────────────────
        // Preview→Adjust→Preview→Execute→Verify pattern
        private static ConversationChain Chain20_DryRunExecuteVN() => new("DryRun Execute VN", new()
        {
            CS("liệt kê ống gió tầng 1", PromptIntent.Query, "Ducts", new[]{"get_elements"}),
            CS("xem trước đổi kích thước 400mm", PromptIntent.Modify, "Ducts", new[]{"resize_mep_elements"}, args: A("diameter_mm", 400)),
            CS("thử 500x300 xem sao", PromptIntent.Modify, "Ducts", new[]{"resize_mep_elements"}, args: A2("width_mm", 500, "height_mm", 300)),
            CS("ok 500x300 được rồi thực hiện", PromptIntent.Modify, "Ducts", new[]{"resize_mep_elements"}),
            CS("kiểm tra vận tốc", PromptIntent.Check, "Ducts", new[]{"check_velocity"}),
            CS("xem trước đổi tên Mark thành D-", PromptIntent.Modify, "Ducts", new[]{"rename_elements"}),
            CS("thực hiện đổi tên", PromptIntent.Modify, "Ducts", new[]{"rename_elements"}),
            CS("mô phỏng thêm bảo ôn 25mm", PromptIntent.Modify, "Ducts", new[]{"add_change_insulation"}, args: A("thickness_mm", 25)),
            CS("áp dụng bảo ôn", PromptIntent.Modify, "Ducts", new[]{"add_change_insulation"}),
            CS("kiểm tra bảo ôn", PromptIntent.Check, null, new[]{"check_insulation_coverage"}),
            CS("xem trước xóa phụ kiện", PromptIntent.Delete, "Duct Accessories", new[]{"delete_elements"}),
            CS("thực hiện xóa", PromptIntent.Delete, null, new[]{"delete_elements"}),
            CS("kiểm tra model", PromptIntent.Check, null, new[]{"audit_model_standards"}),
        });

        // ── Chain 21: Room & Space Workflow ───────────────────────
        // Rooms on multiple levels + energy data
        private static ConversationChain Chain21_RoomSpaceWorkflow() => new("Room Space Workflow", new()
        {
            CS("find all rooms on level 1", PromptIntent.Query, "Rooms", new[]{"get_rooms","get_rooms_detailed"}),
            CS("how many rooms", PromptIntent.Count, "Rooms", new[]{"count_elements"}),
            CS("get room details", PromptIntent.Query, "Rooms", new[]{"get_rooms_detailed"}),
            CS("rooms on level 2", PromptIntent.Query, "Rooms", new[]{"get_rooms"}),
            CS("count those", PromptIntent.Count, "Rooms", new[]{"count_elements"}),
            CS("get space energy data", PromptIntent.Query, null, new[]{"get_space_energy_data"}),
            CS("analyze room areas", PromptIntent.Analyze, "Rooms", new[]{"mep_quantity_takeoff"}),
            CS("export rooms to JSON", PromptIntent.Export, "Rooms", new[]{"export_to_json"}),
            CS("create new room on level 3", PromptIntent.Create, "Rooms", new[]{"create_element"}),
            CS("tag all rooms", PromptIntent.Tag, "Rooms", new[]{"tag_all_in_view"}),
            CS("color rooms by area", PromptIntent.Visual, "Rooms", new[]{"override_color_by_parameter"}),
            CS("export to CSV", PromptIntent.Export, "Rooms", new[]{"export_to_csv"}),
            CS("get view schedules", PromptIntent.Query, null, new[]{"get_view_schedules"}),
        });

        // ── Chain 22: Complete Project Review ─────────────────────
        // Full audit: model health → per-system → per-category → export
        private static ConversationChain Chain22_ProjectReview() => new("Complete Project Review", new()
        {
            CS("audit model standards", PromptIntent.Check, null, new[]{"audit_model_standards"}),
            CS("check all warnings", PromptIntent.Check, null, new[]{"get_model_warnings"}),
            CS("count total elements", PromptIntent.Count, null, new[]{"count_elements"}),
            CS("list ducts all levels", PromptIntent.Query, "Ducts", new[]{"get_elements"}),
            CS("count pipes", PromptIntent.Count, "Pipes", new[]{"count_elements"}),
            CS("analyze Supply Air system", PromptIntent.Analyze, null, new[]{"calculate_system_totals"}, system: "Supply Air"),
            CS("same for Exhaust Air", PromptIntent.Analyze, null, new[]{"calculate_system_totals"}, system: "Exhaust Air"),
            CS("check velocity all ducts", PromptIntent.Check, "Ducts", new[]{"check_velocity"}),
            CS("check slope all pipes", PromptIntent.Check, "Pipes", new[]{"check_pipe_slope"}),
            CS("kiểm tra bảo ôn", PromptIntent.Check, null, new[]{"check_insulation_coverage"}),
            CS("kiểm tra va chạm", PromptIntent.Check, null, new[]{"check_clashes"}),
            CS("export full BOQ", PromptIntent.Export, null, new[]{"export_mep_boq"}),
            CS("export to IFC", PromptIntent.Export, null, new[]{"export_ifc"}),
            CS("screenshot all views", PromptIntent.Visual, null, new[]{"screenshot_view"}),
        });

        // ── Chain 23: Plumbing Workflow ───────────────────────────
        // Plumbing fixtures → Sanitary → DW → Condensate
        private static ConversationChain Chain23_PlumbingWorkflow() => new("Plumbing Workflow", new()
        {
            CS("list plumbing fixtures level 1", PromptIntent.Query, "Plumbing Fixtures", new[]{"get_plumbing_fixtures","get_elements"}),
            CS("count them", PromptIntent.Count, "Plumbing Fixtures", new[]{"count_elements"}),
            CS("show sanitary pipes", PromptIntent.Query, "Pipes", new[]{"get_elements"}, system: "Sanitary"),
            CS("check slope", PromptIntent.Check, "Pipes", new[]{"check_pipe_slope"}),
            CS("set slope 1.5%", PromptIntent.Modify, "Pipes", new[]{"set_pipe_slope"}, args: A("slope_pct", 1.5)),
            CS("check domestic water pipes", PromptIntent.Check, "Pipes", new[]{"check_disconnected_elements"}, system: "Domestic Water"),
            CS("count those", PromptIntent.Count, "Pipes", new[]{"count_elements"}),
            CS("list condensate pipes", PromptIntent.Query, "Pipes", new[]{"get_elements"}, system: "Condensate"),
            CS("check connections", PromptIntent.Check, "Pipes", new[]{"check_disconnected_elements"}),
            CS("export plumbing BOQ", PromptIntent.Export, "Pipes", new[]{"export_mep_boq"}),
            CS("tag all fixtures", PromptIntent.Tag, "Plumbing Fixtures", new[]{"tag_all_in_view"}),
            CS("color pipes by system", PromptIntent.Visual, "Pipes", new[]{"override_color_by_system"}),
            CS("export to CSV", PromptIntent.Export, "Pipes", new[]{"export_to_csv"}),
        });

        // ── Chain 24: Fire Protection Workflow ────────────────────
        // Sprinklers → FP pipes → fix → verify → report
        private static ConversationChain Chain24_FireProtectionWorkflow() => new("Fire Protection Workflow", new()
        {
            CS("list sprinklers level 1", PromptIntent.Query, "Sprinklers", new[]{"get_fire_protection_equipment","get_elements"}),
            CS("count them", PromptIntent.Count, "Sprinklers", new[]{"count_elements"}),
            CS("sprinklers on level 2", PromptIntent.Query, "Sprinklers", new[]{"get_elements"}),
            CS("count those", PromptIntent.Count, "Sprinklers", new[]{"count_elements"}),
            CS("show FP pipes", PromptIntent.Query, "Pipes", new[]{"get_elements"}, system: "Fire Protection"),
            CS("check connections", PromptIntent.Check, "Pipes", new[]{"check_disconnected_elements"}),
            CS("count disconnected", PromptIntent.Count, "Pipes", new[]{"count_elements"}),
            CS("fix disconnections", PromptIntent.Modify, "Pipes", new[]{"connect_mep_elements"}),
            CS("verify connections", PromptIntent.Check, "Pipes", new[]{"check_disconnected_elements"}),
            CS("tag all sprinklers", PromptIntent.Tag, "Sprinklers", new[]{"tag_all_in_view"}),
            CS("color FP system red", PromptIntent.Visual, null, new[]{"override_color_by_system"}, system: "Fire Protection"),
            CS("export FP summary to CSV", PromptIntent.Export, null, new[]{"export_to_csv"}),
            CS("screenshot view", PromptIntent.Visual, null, new[]{"screenshot_view"}),
        });

        #endregion

        // ═════════════════════════════════════════════════════════
        //  HELPERS
        // ═════════════════════════════════════════════════════════

        private static Dictionary<string, object> A(string k, object v) => new() { [k] = v };
        private static Dictionary<string, object> A2(string k1, object v1, string k2, object v2) => new() { [k1] = v1, [k2] = v2 };
    }

    // ═════════════════════════════════════════════════════════
    //  DATA CLASSES
    // ═════════════════════════════════════════════════════════

    public class TrainingSample
    {
        public string Prompt { get; }
        public PromptIntent ExpectedIntent { get; }
        public string ExpectedCategory { get; }
        public string ExpectedSystem { get; }
        public List<string> ExpectedTools { get; }
        public Dictionary<string, object> ExpectedArgs { get; }

        public TrainingSample(string prompt, PromptIntent intent, string category,
            string[] tools, Dictionary<string, object> args = null, string system = null)
        {
            Prompt = prompt;
            ExpectedIntent = intent;
            ExpectedCategory = category;
            ExpectedSystem = system;
            ExpectedTools = tools?.ToList() ?? new();
            ExpectedArgs = args ?? new();
        }
    }

    public class ConversationChain
    {
        public string Name { get; }
        public List<TrainingSample> Steps { get; }

        public ConversationChain(string name, List<TrainingSample> steps)
        {
            Name = name;
            Steps = steps;
        }
    }

    public class EpochResult
    {
        public int Epoch { get; set; }
        public int Matched { get; set; }
        public int IntentCorrect { get; set; }
        public int CategoryCorrect { get; set; }
        public int ToolCorrect { get; set; }
    }

    public class TrainingReport
    {
        public int TotalSamples { get; set; }
        public int Epochs { get; set; }
        public int Matched { get; set; }
        public int IntentCorrect { get; set; }
        public int CategoryCorrect { get; set; }
        public int ToolCorrect { get; set; }
        public int FewShotAdded { get; set; }
        public int WeightsAdjusted { get; set; }
        public int Errors { get; set; }
        public long ElapsedMs { get; set; }
        public bool EmbeddingsQueued { get; set; }
        public List<EpochResult> EpochResults { get; set; } = new();

        public int ConversationChains { get; set; }
        public int ConversationSteps { get; set; }
        public int ConversationMatched { get; set; }

        public int TotalProcessed => TotalSamples * Epochs;
        public double IntentAccuracy => TotalProcessed > 0 ? (double)IntentCorrect / TotalProcessed * 100 : 0;
        public double CategoryAccuracy => TotalProcessed > 0 ? (double)CategoryCorrect / TotalProcessed * 100 : 0;
        public double ToolAccuracy => TotalProcessed > 0 ? (double)ToolCorrect / TotalProcessed * 100 : 0;
        public double OverallAccuracy => TotalProcessed > 0 ? (double)Matched / TotalProcessed * 100 : 0;
        public int ConversationTotalProcessed { get; set; }
        public double ConversationAccuracy => ConversationTotalProcessed > 0
            ? (double)ConversationMatched / ConversationTotalProcessed * 100 : 0;

        public override string ToString() =>
            $"Training ({Epochs} epochs): {Matched}/{TotalProcessed} ({OverallAccuracy:F1}%) | " +
            $"Intent: {IntentAccuracy:F1}% | Category: {CategoryAccuracy:F1}% | Tool: {ToolAccuracy:F1}% | " +
            $"Conv: {ConversationMatched}/{ConversationSteps} ({ConversationAccuracy:F1}%) | " +
            $"{FewShotAdded} examples, {WeightsAdjusted} weights | {ElapsedMs}ms";
    }
}
