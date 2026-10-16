using System.ComponentModel.DataAnnotations;
using System.Text;
using Ccms.BuildingBlocks.Web;
using Ccms.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Hosting;

namespace Ccms.Modules.Signatures;

/// <summary>صفحة المزوّد التجريبي (تحاكي ما يجري لدى مزوّد خارجي) وحقن الأعطال للاختبار. غير متاحة في الإنتاج أو حين لا يكون المزوّد mock.</summary>
internal static class MockEndpoints
{
    public static void Map(RouteGroupBuilder g)
    {
        var m = g.MapGroup("/mock");
        m.MapGet("/sessions/{token}", Session);
        m.MapPost("/sessions/{token}/sign", Sign);
        m.MapPost("/sessions/{token}/decline", Decline).Validate<SignaturesModule.ReasonBody>();
        m.MapGet("/faults", GetFaults);
        m.MapPut("/faults", SetFaults);
    }

    public sealed record FaultsRequest(bool Unavailable, [property: Range(0, 100)] int FailNext);

    private static async Task GuardAsync(IHostEnvironment env, IFeatureService features, CancellationToken ct)
    {
        if (env.IsProduction() || await features.GetValueAsync(FeatureKeys.ESignatureProvider, ct) != "mock") throw new NotFoundException();
    }

    private static async Task<IResult> Session(string token, MockSignatureProvider mock, IHostEnvironment env, IFeatureService features, ICurrentUser me, CancellationToken ct)
    {
        await GuardAsync(env, features, ct);
        var found = await mock.FindByTokenAsync(token, ct) ?? throw new NotFoundException("جلسة التوقيع غير موجودة");
        var (e, s) = found;
        if (s["userId"]!.GetValue<Guid>() != me.UserId) throw new AccessDeniedException("not-your-session", "جلسة التوقيع لموقّع آخر");
        var order = s["order"]!.GetValue<int>();
        return Results.Ok(new
        {
            testOnly = true, notice = "مزوّد تجريبي للاختبار فقط — ليس خدمة توقيع إنتاجية ولا يحقق أي حجية نظامية",
            providerRef = e.ProviderRef, reference = e.Reference, envelopeStatus = e.Status, signerName = s["name"]!.GetValue<string>(), signerStatus = s["status"]!.GetValue<string>(),
            order, sequential = e.Sequential, documentSha256 = e.OriginalSha256, expiresAt = e.ExpiresAt,
            turn = !e.Sequential || e.Signers.All(x => x!["order"]!.GetValue<int>() >= order || x["status"]!.GetValue<string>() == "signed"),
            signers = e.Signers.Select(x => new { name = x!["name"]!.GetValue<string>(), order = x["order"]!.GetValue<int>(), status = x["status"]!.GetValue<string>() }),
        });
    }

    /// <summary>يوقّع لدى المزوّد ثم «يسلّم» الاستدعاءات الراجعة الموقعة إلى التطبيق عبر المسار نفسه الذي يستقبل استدعاءات المزوّدين.</summary>
    private static async Task<IResult> Sign(string token, MockSignatureProvider mock, SigningService svc, IHostEnvironment env, IFeatureService features, ICurrentUser me,
        CancellationToken ct)
    {
        await GuardAsync(env, features, ct);
        try { return await DeliverAsync(await mock.SignAtProviderAsync(token, me.UserId, ct), mock, svc, ct); }
        catch (MockSessionException e) { throw new ConflictException(e.Code, e.Message); }
        catch (SignatureProviderUnavailableException e) { throw new SigUnavailableException(e.Message); }
    }

    private static async Task<IResult> Decline(string token, SignaturesModule.ReasonBody req, MockSignatureProvider mock, SigningService svc, IHostEnvironment env,
        IFeatureService features, ICurrentUser me, CancellationToken ct)
    {
        await GuardAsync(env, features, ct);
        try { return await DeliverAsync(await mock.DeclineAtProviderAsync(token, me.UserId, req.Reason, ct), mock, svc, ct); }
        catch (MockSessionException e) { throw new ConflictException(e.Code, e.Message); }
        catch (SignatureProviderUnavailableException e) { throw new SigUnavailableException(e.Message); }
    }

    private static async Task<IResult> DeliverAsync(List<(string Body, string Signature)> callbacks, MockSignatureProvider mock, SigningService svc, CancellationToken ct)
    {
        var outcomes = new List<object>();
        foreach (var (body, signature) in callbacks)
        {
            var headers = new HeaderDictionary { [MockSignatureProvider.SignatureHeader] = signature };
            var result = await svc.ProcessCallbackAsync(mock, headers, Encoding.UTF8.GetBytes(body), ct);
            outcomes.Add(new { delivered = true, status = (result as IStatusCodeHttpResult)?.StatusCode ?? 200 });
        }
        return Results.Ok(new { callbacks = outcomes });
    }

    private static async Task<IResult> GetFaults(MockFaults faults, IAccessService access, IHostEnvironment env, CancellationToken ct)
    {
        await access.DemandAsync(Permissions.AdminSettingsManage, Scope.System, ct);
        if (env.IsProduction()) throw new NotFoundException();
        return Results.Ok(new { faults.Unavailable, faults.FailNext });
    }

    private static async Task<IResult> SetFaults(FaultsRequest req, MockFaults faults, IAccessService access, IHostEnvironment env, IAuditWriter audit, CancellationToken ct)
    {
        await access.DemandAsync(Permissions.AdminSettingsManage, Scope.System, ct);
        if (env.IsProduction()) throw new NotFoundException();
        faults.Unavailable = req.Unavailable;
        faults.FailNext = req.FailNext;
        await audit.WriteAsync(new AuditEntry("SIG", "esign.mock_faults_set", "system", "mock", After: req), ct);
        return Results.Ok(new { faults.Unavailable, faults.FailNext });
    }
}
