using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using System.Text.Json.Serialization.Metadata;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Mvc.ApiExplorer;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace Ccms.Host;

/// <summary>يولّد وثيقة OpenAPI 3.1 من مستكشف الواجهات المدمج في ASP.NET Core ومن مصدّر JSON Schema في System.Text.Json.
/// يُستخدم بدل Microsoft.AspNetCore.OpenApi لأن بيئة البناء الحالية بلا NuGet (ADR-015)؛ المخرَج وثيقة OpenAPI قياسية.</summary>
internal sealed class OpenApiDocumentBuilder(IApiDescriptionGroupCollectionProvider explorer, IConfiguration configuration)
{
    private static readonly JsonSerializerOptions SchemaOptions = new(JsonSerializerDefaults.Web) { TypeInfoResolver = new DefaultJsonTypeInfoResolver() };

    public JsonObject Build()
    {
        var schemas = new JsonObject();
        var paths = new JsonObject();

        foreach (var d in explorer.ApiDescriptionGroups.Items.SelectMany(g => g.Items)
                     .Where(d => d.RelativePath is not null)
                     .OrderBy(d => d.RelativePath).ThenBy(d => d.HttpMethod))
        {
            var path = "/" + d.RelativePath!.Split('?')[0].Replace(":guid", "", StringComparison.Ordinal);
            path = System.Text.RegularExpressions.Regex.Replace(path, @"\{(\w+):[^}]+\}", "{$1}");
            if (path.Length > 1) path = path.TrimEnd('/');
            var item = paths[path] as JsonObject ?? new JsonObject();
            paths[path] = item;

            var tags = d.ActionDescriptor.EndpointMetadata.OfType<ITagsMetadata>().SelectMany(t => t.Tags).Distinct().ToArray();
            var anonymous = d.ActionDescriptor.EndpointMetadata.OfType<Microsoft.AspNetCore.Authorization.IAllowAnonymous>().Any();
            var op = new JsonObject
            {
                ["operationId"] = $"{d.HttpMethod?.ToLowerInvariant()}_{path.Trim('/').Replace('/', '_').Replace("{", "").Replace("}", "")}",
                ["tags"] = new JsonArray(tags.Select(t => (JsonNode)t).ToArray()),
            };
            if (!anonymous) op["security"] = new JsonArray(new JsonObject { ["session"] = new JsonArray() });

            var parameters = new JsonArray();
            foreach (var p in d.ParameterDescriptions)
            {
                if (p.Source == BindingSource.Path || p.Source == BindingSource.Query || p.Source == BindingSource.ModelBinding && p.Type?.IsPrimitiveLike() == true)
                {
                    parameters.Add(new JsonObject
                    {
                        ["name"] = p.Name,
                        ["in"] = p.Source == BindingSource.Path ? "path" : "query",
                        ["required"] = p.Source == BindingSource.Path || p.IsRequired,
                        ["schema"] = Schema(p.Type ?? typeof(string), schemas),
                    });
                }
                else if (p.Source == BindingSource.Body && p.Type is not null)
                {
                    op["requestBody"] = new JsonObject
                    {
                        ["required"] = true,
                        ["content"] = new JsonObject { ["application/json"] = new JsonObject { ["schema"] = Schema(p.Type, schemas) } },
                    };
                }
            }
            if (parameters.Count > 0) op["parameters"] = parameters;
            if (path.EndsWith("/documents") && d.HttpMethod == "POST")
                op["requestBody"] = new JsonObject
                {
                    ["content"] = new JsonObject
                    {
                        ["multipart/form-data"] = new JsonObject
                        {
                            ["schema"] = JsonNode.Parse("""{"type":"object","required":["file","ownerType","ownerId"],"properties":{"file":{"type":"string","format":"binary"},"ownerType":{"type":"string","enum":["body","meeting","minutes","resolution","task"]},"ownerId":{"type":"string","format":"uuid"},"purpose":{"type":"string"}}}"""),
                        },
                    },
                };

            var problem = new JsonObject { ["$ref"] = "#/components/schemas/ProblemDetails" };
            var responses = new JsonObject { ["200"] = new JsonObject { ["description"] = "نجاح" } };
            foreach (var (code, text) in new[] { ("400", "بيانات غير صالحة"), ("401", "يلزم تسجيل الدخول"), ("403", "لا صلاحية"), ("404", "غير موجود أو خارج النطاق أو خاصية معطلة"), ("409", "تعارض حالة أو يتطلب تأكيدًا"), ("503", "مزوّد الخدمة غير متاح") })
                responses[code] = new JsonObject
                {
                    ["description"] = text,
                    ["content"] = new JsonObject { ["application/problem+json"] = new JsonObject { ["schema"] = problem.DeepClone() } },
                };
            op["responses"] = responses;
            item[d.HttpMethod!.ToLowerInvariant()] = op;
        }

        schemas["ProblemDetails"] = JsonNode.Parse("""
            {"type":"object","properties":{"type":{"type":"string"},"title":{"type":"string"},"status":{"type":"integer"},"detail":{"type":"string"},
             "instance":{"type":"string"},"code":{"type":"string"},"traceId":{"type":"string"},"errors":{"type":"object","additionalProperties":{"type":"array","items":{"type":"string"}}}}}
            """);

        return new JsonObject
        {
            ["openapi"] = "3.1.0",
            ["info"] = new JsonObject
            {
                ["title"] = "Aljouf Councils & Committees Management System API",
                ["version"] = configuration["App:Version"] ?? "0.1.0",
                ["description"] = "واجهات نظام إدارة المجالس واللجان وفرق العمل — المكتب الاستراتيجي لتطوير منطقة الجوف. كل الطلبات غير GET تتطلب ترويسة X-CSRF-Token من /api/v1/auth/csrf.",
            },
            ["servers"] = new JsonArray(new JsonObject { ["url"] = "/" }),
            ["paths"] = paths,
            ["components"] = new JsonObject
            {
                ["schemas"] = schemas,
                ["securitySchemes"] = new JsonObject
                {
                    ["session"] = new JsonObject { ["type"] = "apiKey", ["in"] = "cookie", ["name"] = "ccms.session" },
                },
            },
        };
    }

    private static JsonNode Schema(Type type, JsonObject schemas)
    {
        var t = Nullable.GetUnderlyingType(type) ?? type;
        if (t.IsPrimitiveLike()) return JsonSchemaExporter.GetJsonSchemaAsNode(SchemaOptions, t);
        var name = t.FullName!.Split('.').Last().Replace('+', '_');
        if (!schemas.ContainsKey(name))
        {
            schemas[name] = new JsonObject(); // يمنع التكرار اللانهائي
            schemas[name] = JsonSchemaExporter.GetJsonSchemaAsNode(SchemaOptions, t, new JsonSchemaExporterOptions { TreatNullObliviousAsNonNullable = true });
        }
        return new JsonObject { ["$ref"] = $"#/components/schemas/{name}" };
    }
}

internal static class TypeExtensions
{
    public static bool IsPrimitiveLike(this Type t)
    {
        t = Nullable.GetUnderlyingType(t) ?? t;
        return t.IsPrimitive || t.IsEnum || t == typeof(string) || t == typeof(Guid) || t == typeof(DateTime) || t == typeof(DateOnly) || t == typeof(decimal);
    }
}
