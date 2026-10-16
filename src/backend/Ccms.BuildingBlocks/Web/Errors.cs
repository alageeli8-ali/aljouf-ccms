using System.ComponentModel.DataAnnotations;
using System.Data.Common;
using Ccms.BuildingBlocks.Data;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Ccms.BuildingBlocks.Web;

/// <summary>استثناءات الأعمال؛ كل منها يُترجم إلى ProblemDetails برمز من 11 §1.</summary>
public abstract class AppException(string code, int status, string message, IDictionary<string, object?>? extensions = null)
    : Exception(message)
{
    public string Code { get; } = code;
    public int Status { get; } = status;
    public IDictionary<string, object?> Extensions { get; } = extensions ?? new Dictionary<string, object?>();
}

public sealed class NotFoundException(string message = "السجل غير موجود") : AppException("not-found", 404, message);

public sealed class ForbiddenException(string message = "لا تملك صلاحية تنفيذ هذا الإجراء", string? permission = null)
    : AppException("forbidden-action", 403, message, new Dictionary<string, object?> { ["permission"] = permission });

public sealed class ConflictException(string code, string message, IDictionary<string, object?>? extensions = null)
    : AppException(code, 409, message, extensions);

public sealed class AppValidationException(IDictionary<string, string[]> errors, string message = "البيانات المدخلة غير صالحة")
    : AppException("validation", 400, message)
{
    public IDictionary<string, string[]> Errors { get; } = errors;

    public static AppValidationException For(string field, string error) => new(new Dictionary<string, string[]> { [field] = [error] });
}

public sealed class FeatureDisabledException(string featureKey)
    : AppException("feature-disabled", 404, $"الخاصية {featureKey} معطلة", new Dictionary<string, object?> { ["feature"] = featureKey });

public sealed class ProviderUnavailableException(string connector, string status)
    : AppException("provider-unavailable", 503, $"الخاصية مفعلة لكن مزوّد الخدمة ({connector}) غير متاح: {status}",
        new Dictionary<string, object?> { ["connector"] = connector, ["connectorStatus"] = status });

/// <summary>رفض بسبب سياسة بنوع محدد (403)، مثل اشتراط تغيير كلمة المرور أو تعطيل البوابة.</summary>
public sealed class AccessDeniedException(string code, string message) : AppException(code, 403, message);

public sealed class UnauthenticatedException(string message = "يلزم تسجيل الدخول") : AppException("unauthenticated", 401, message);

public sealed class AppExceptionHandler(ILogger<AppExceptionHandler> logger, IProblemDetailsService problems) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext http, Exception exception, CancellationToken ct)
    {
        ProblemDetails pd;
        switch (exception)
        {
            case AppValidationException v:
                pd = new ValidationProblemDetails(v.Errors) { Status = 400, Title = v.Message };
                pd.Type = "/problems/validation";
                break;
            case AppException a:
                pd = new ProblemDetails { Status = a.Status, Title = a.Message, Type = $"/problems/{a.Code}" };
                foreach (var (k, val) in a.Extensions) pd.Extensions[k] = val;
                break;
            case BadHttpRequestException b:
                pd = new ProblemDetails { Status = b.StatusCode, Title = "طلب غير صالح", Type = "/problems/validation", Detail = b.Message };
                break;
            case DbException db when db.SqlState == SqlErrors.UniqueViolation:
                pd = new ProblemDetails { Status = 409, Title = "السجل موجود مسبقًا", Type = "/problems/duplicate" };
                break;
            case DbException db when db.SqlState is SqlErrors.ForeignKeyViolation or SqlErrors.CheckViolation:
                pd = new ProblemDetails { Status = 400, Title = "البيانات تخالف قيود السجل", Type = "/problems/validation" };
                break;
            default:
                logger.LogError(exception, "خطأ غير متوقع في {Path}", http.Request.Path);
                pd = new ProblemDetails { Status = 500, Title = "حدث خطأ غير متوقع", Type = "/problems/internal" };
                break;
        }
        pd.Instance = http.Request.Path;
        pd.Extensions["traceId"] = http.TraceIdentifier;
        pd.Extensions["code"] = pd.Type!.Replace("/problems/", "");
        http.Response.StatusCode = pd.Status ?? 500;
        return await problems.TryWriteAsync(new ProblemDetailsContext { HttpContext = http, ProblemDetails = pd, Exception = exception });
    }
}

/// <summary>تحقق المدخلات بسمات DataAnnotations وIValidatableObject قبل تنفيذ نقطة النهاية.</summary>
public sealed class ValidationFilter<T> : IEndpointFilter where T : class
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext ctx, EndpointFilterDelegate next)
    {
        var arg = ctx.Arguments.OfType<T>().FirstOrDefault()
            ?? throw AppValidationException.For("body", "جسم الطلب مطلوب");
        Validate(arg);
        return await next(ctx);
    }

    public static void Validate(object arg)
    {
        var results = new List<ValidationResult>();
        if (Validator.TryValidateObject(arg, new ValidationContext(arg), results, validateAllProperties: true)) return;
        var errors = results
            .SelectMany(r => (r.MemberNames.Any() ? r.MemberNames : [""]).Select(m => (Member: Camel(m), r.ErrorMessage ?? "قيمة غير صالحة")))
            .GroupBy(x => x.Member)
            .ToDictionary(g => g.Key, g => g.Select(x => x.Item2).ToArray());
        throw new AppValidationException(errors);
    }

    private static string Camel(string s) => string.IsNullOrEmpty(s) ? s : char.ToLowerInvariant(s[0]) + s[1..];
}

public static class ValidationExtensions
{
    public static RouteHandlerBuilder Validate<T>(this RouteHandlerBuilder b) where T : class
        => b.AddEndpointFilter<ValidationFilter<T>>().ProducesValidationProblem();
}
