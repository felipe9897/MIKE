using System.Text.Json;

namespace Mike.Common;

public enum EvolutionRisk { Reversible, Sensitive, Administrative }

public sealed record EvolutionAgent(
    string Id,
    string Name,
    string Domain,
    EvolutionRisk MaximumAutomaticRisk,
    string[] Capabilities);

public sealed record EvolutionEvaluation(
    double Quality,
    double Reliability,
    double Safety,
    double Efficiency,
    DateTimeOffset RecordedAt);

public sealed class EvolutionCandidate
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string AgentId { get; set; } = "";
    public string Domain { get; set; } = "";
    public string Description { get; set; } = "";
    public EvolutionRisk Risk { get; set; }
    public string Status { get; set; } = "quarantine";
    public double BaselineScore { get; set; }
    public List<EvolutionEvaluation> Evaluations { get; set; } = [];
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? PromotedAt { get; set; }
}

public sealed class AutonomousEvolution
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly string statePath;
    private readonly object gate = new();

    public static IReadOnlyList<EvolutionAgent> Agents { get; } = new[]
    {
        new EvolutionAgent("evolution-manager", "Gestor de Evolucao", "global", EvolutionRisk.Reversible,
            ["observe", "benchmark", "quarantine", "promote", "rollback"]),
        new EvolutionAgent("translation-specialist", "Especialista de Traducao", "translation", EvolutionRisk.Reversible,
            ["ocr", "translation", "subtitle", "dubbing", "quality-gate"]),
        new EvolutionAgent("programming-specialist", "Especialista de Programacao", "programming", EvolutionRisk.Reversible,
            ["diagnose", "patch", "test", "review", "rollback"]),
        new EvolutionAgent("self-maintainer", "Mantenedor do Proprio Mike", "self-code", EvolutionRisk.Sensitive,
            ["snapshot", "branch", "patch", "build", "test", "canary", "rollback"]),
        new EvolutionAgent("windows-operator", "Operador Windows", "computer", EvolutionRisk.Sensitive,
            ["observe-ui", "uia", "launch", "input", "verify"]),
        new EvolutionAgent("companion", "Mike Companion", "mobile", EvolutionRisk.Reversible,
            ["text", "voice", "camera", "notifications", "approvals"]),
        new EvolutionAgent("fleet-administrator", "Administrador de PCs", "fleet", EvolutionRisk.Administrative,
            ["pair", "inventory", "remote-support", "install", "audit"]),
    };

    public AutonomousEvolution(string dataRoot)
    {
        Directory.CreateDirectory(dataRoot);
        statePath = Path.Combine(dataRoot, "autonomous-evolution.json");
    }

    public IReadOnlyList<EvolutionCandidate> ReadCandidates()
    {
        lock (gate) return Load().Candidates.ToArray();
    }

    public EvolutionCandidate Propose(string agentId, string domain, string description,
        EvolutionRisk risk, double baselineScore)
    {
        if (!Agents.Any(agent => agent.Id == agentId))
            throw new ArgumentException("Unknown evolution agent.", nameof(agentId));
        lock (gate)
        {
            EvolutionState state = Load();
            var candidate = new EvolutionCandidate {
                AgentId = agentId, Domain = domain, Description = description,
                Risk = risk, BaselineScore = Clamp(baselineScore)
            };
            state.Candidates.Add(candidate);
            Save(state);
            return candidate;
        }
    }

    public EvolutionCandidate Evaluate(string id, EvolutionEvaluation evaluation)
    {
        lock (gate)
        {
            EvolutionState state = Load();
            EvolutionCandidate candidate = Find(state, id);
            candidate.Evaluations.Add(evaluation with {
                Quality = Clamp(evaluation.Quality), Reliability = Clamp(evaluation.Reliability),
                Safety = Clamp(evaluation.Safety), Efficiency = Clamp(evaluation.Efficiency)
            });
            TryPromote(candidate);
            Save(state);
            return candidate;
        }
    }

    public EvolutionCandidate Rollback(string id, string reason)
    {
        lock (gate)
        {
            EvolutionState state = Load();
            EvolutionCandidate candidate = Find(state, id);
            candidate.Status = "rolled_back";
            candidate.PromotedAt = null;
            state.Events.Add(new EvolutionEvent(id, "rollback", reason, DateTimeOffset.UtcNow));
            Save(state);
            return candidate;
        }
    }

    public EvolutionCandidate RecordOutcome(string agentId, string domain, string description,
        bool success, double quality, double reliability, double safety, double efficiency)
    {
        if (!Agents.Any(agent => agent.Id == agentId))
            throw new ArgumentException("Unknown evolution agent.", nameof(agentId));
        lock (gate)
        {
            EvolutionState state = Load();
            EvolutionCandidate? candidate = state.Candidates.LastOrDefault(item =>
                item.AgentId == agentId && item.Domain == domain && item.Status == "quarantine");
            candidate ??= new EvolutionCandidate {
                AgentId = agentId, Domain = domain, Description = description,
                Risk = EvolutionRisk.Reversible, BaselineScore = 0.65
            };
            if (!state.Candidates.Contains(candidate)) state.Candidates.Add(candidate);
            candidate.Evaluations.Add(new EvolutionEvaluation(
                Clamp(success ? quality : Math.Min(quality, 0.30)),
                Clamp(success ? reliability : Math.Min(reliability, 0.30)),
                Clamp(safety), Clamp(efficiency), DateTimeOffset.UtcNow));
            TryPromote(candidate);
            Save(state);
            return candidate;
        }
    }

    private static void TryPromote(EvolutionCandidate candidate)
    {
        if (candidate.Risk != EvolutionRisk.Reversible || candidate.Evaluations.Count < 3)
            return;
        EvolutionEvaluation[] recent = candidate.Evaluations.TakeLast(3).ToArray();
        double score = recent.Average(Score);
        if (recent.All(item => item.Safety >= 0.95 && item.Reliability >= 0.80)
            && score >= candidate.BaselineScore + 0.05)
        {
            candidate.Status = "promoted";
            candidate.PromotedAt = DateTimeOffset.UtcNow;
        }
    }

    private static double Score(EvolutionEvaluation value) =>
        value.Quality * 0.40 + value.Reliability * 0.30 + value.Safety * 0.20 + value.Efficiency * 0.10;
    private static double Clamp(double value) => Math.Clamp(value, 0, 1);
    private static EvolutionCandidate Find(EvolutionState state, string id) =>
        state.Candidates.FirstOrDefault(item => item.Id == id)
        ?? throw new KeyNotFoundException("Evolution candidate not found.");

    private EvolutionState Load()
    {
        if (!File.Exists(statePath)) return new EvolutionState();
        try { return JsonSerializer.Deserialize<EvolutionState>(File.ReadAllText(statePath), JsonOptions) ?? new(); }
        catch (JsonException) { return new EvolutionState(); }
    }

    private void Save(EvolutionState state)
    {
        string temporary = statePath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(state, JsonOptions));
        File.Move(temporary, statePath, true);
    }

    private sealed class EvolutionState
    {
        public List<EvolutionCandidate> Candidates { get; set; } = [];
        public List<EvolutionEvent> Events { get; set; } = [];
    }

    private sealed record EvolutionEvent(string CandidateId, string Action, string Reason, DateTimeOffset At);
}
