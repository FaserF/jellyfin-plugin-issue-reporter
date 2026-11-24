using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.IssueReporter.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.IssueReporter.Api;

[ApiController]
[Authorize(Policy = "RequiresElevation")]
[Route("IssueReporter")]
public sealed class IssueReporterController : ControllerBase
{
    private readonly IssueReporterCoordinator _coordinator;
    private readonly PrivacyRedactorService _redactor;
    private readonly StateStoreService _store;
    private readonly GitHubAuthService _auth;

    public IssueReporterController(
        IssueReporterCoordinator coordinator,
        PrivacyRedactorService redactor,
        StateStoreService store,
        GitHubAuthService auth)
    {
        _coordinator = coordinator;
        _redactor = redactor;
        _store = store;
        _auth = auth;
    }

    [HttpGet("Status")]
    public ActionResult GetStatus()
    {
        return Ok(new
        {
            actor = _auth.GetActorDescription(_coordinator.CurrentConfig),
            stats = _store.Stats,
            targets = _coordinator.Targets.Values.Select(t => new
            {
                t.TargetKey,
                t.Name,
                t.AssemblyName,
                t.Version,
                t.FullRepo,
                t.Channel,
                t.OwnerMatched,
                t.Enabled,
                t.Mode,
                minSeverity = t.MinSeverity.ToString(),
            }),
            drafts = _store.GetDrafts(),
        });
    }

    [HttpPost("ScanNow")]
    public ActionResult ScanNow()
    {
        _coordinator.RefreshConfiguration();
        return Ok(new { targetsCount = _coordinator.Targets.Count });
    }

    [HttpPost("TestRedaction")]
    public ActionResult TestRedaction([FromBody] RedactionRequest req)
    {
        var res = _redactor.Redact(req.Text ?? string.Empty);
        var (passed, reasons) = _redactor.VerifyNoLeaks(string.Empty, res.Text);
        return Ok(new
        {
            redactedText = res.Text,
            replacementsCount = res.ReplacementsCount,
            matchedCategories = res.MatchedCategories,
            leakGuardPassed = passed,
            leakGuardReasons = reasons,
        });
    }

    [HttpPost("PreviewDraft")]
    public async Task<ActionResult> PreviewDraft([FromBody] PreviewRequest req, CancellationToken ct)
    {
        var result = await _coordinator.PreviewDraftAsync(
            req.TargetKey,
            req.SampleMessage ?? "Sample plugin error",
            req.Severity ?? "ERROR",
            ct);
        return Ok(result);
    }

    [HttpPost("ApproveDraft/{draftId}")]
    public async Task<ActionResult> ApproveDraft(string draftId, CancellationToken ct)
    {
        var url = await _coordinator.ApproveDraftAsync(draftId, ct);
        return Ok(new { issueUrl = url });
    }

    [HttpPost("DismissDraft/{draftId}")]
    public ActionResult DismissDraft(string draftId)
    {
        _store.DismissDraft(draftId);
        _store.Save();
        return Ok();
    }

    [HttpPost("ClearCooldowns")]
    public ActionResult ClearCooldowns()
    {
        _store.ClearCooldowns();
        _store.Save();
        return Ok();
    }

    [HttpPost("DeviceFlow/Start")]
    public async Task<ActionResult> StartDeviceFlow([FromBody] DeviceFlowStartRequest req, CancellationToken ct)
    {
        var (deviceCode, userCode, verificationUri, interval) = await _auth.StartDeviceFlowAsync(req.ClientId, ct);
        return Ok(new { deviceCode, userCode, verificationUri, interval });
    }

    [HttpPost("DeviceFlow/Poll")]
    public async Task<ActionResult> PollDeviceFlow([FromBody] DeviceFlowPollRequest req, CancellationToken ct)
    {
        var (status, token) = await _auth.PollDeviceFlowOnceAsync(req.ClientId, req.DeviceCode, ct);
        if (status == "authorized" && !string.IsNullOrEmpty(token))
        {
            var login = await _auth.ValidatePatAsync(token, ct);
            return Ok(new { status, token, login });
        }

        return Ok(new { status });
    }

    public sealed record RedactionRequest(string? Text);

    public sealed record PreviewRequest(string TargetKey, string? SampleMessage, string? Severity);

    public sealed record DeviceFlowStartRequest(string ClientId);

    public sealed record DeviceFlowPollRequest(string ClientId, string DeviceCode);
}
