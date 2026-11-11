using System.Data;
using System.Globalization;
using System.Text.Json;
using FCG.Catalog.Application.Orders;
using FCG.Catalog.Infrastructure.Orders;
using FCG.Catalog.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FCG.Catalog.Api.Operations;

internal static class PaymentRecoveryCommand
{
    private const string Usage = "Usage: recover-payments list [--limit 1..100] | show --id GUID | replay --id GUID [--dry-run | --apply]";
    public static async Task<int> RunAsync(string[] args)
    {
        var options = Parse(args);
        if (options is null) { Console.Error.WriteLine(Usage); return 2; }
        try
        {
            // Build a local DI scope only: no web host, bus, migrations, keys or seed registration.
            var builder = Host.CreateApplicationBuilder(Array.Empty<string>());
            builder.Logging.ClearProviders();
            var connection = builder.Configuration.GetConnectionString("Default");
            if (string.IsNullOrWhiteSpace(connection)) { Console.Error.WriteLine("Recovery configuration unavailable."); return 1; }
            builder.Services.AddDbContext<CatalogDbContext>(db => db.UseNpgsql(connection));
            builder.Services.AddSingleton<TimeProvider>(TimeProvider.System);
            builder.Services.AddScoped<PaymentApplication>();
            using var host = builder.Build();
            await using var scope = host.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var ct = deadline.Token;
            if (options.Operation == "replay")
            {
                var application = scope.ServiceProvider.GetRequiredService<PaymentApplication>();
                var result = options.Apply ? await application.ReplayAsync(options.Id!.Value, ct) : await application.PreviewReplayAsync(options.Id!.Value, ct);
                Write(new { operation = "replay", conflictId = options.Id, dryRun = !options.Apply, outcome = result.Outcome.ToString(), reason = result.Reason });
                return result.Outcome switch { PaymentApplicationOutcome.NotFound => 3, PaymentApplicationOutcome.Quarantined => 4, _ => 0 };
            }
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
            await db.Database.ExecuteSqlRawAsync("SET TRANSACTION READ ONLY", ct);
            var query = db.MessageConflicts.AsNoTracking();
            if (options.Operation == "show") query = query.Where(x => x.Id == options.Id);
            var records = await query.OrderBy(x => x.Resolved).ThenBy(x => x.FirstSeenAtUtc).ThenBy(x => x.Id)
                .Take(options.Operation == "show" ? 1 : options.Limit)
                .Select(x => new { x.Id, x.Kind, x.BusinessId, x.EventId, x.EventVersion, x.MessageType, x.TransportMessageId,
                    x.Reason, x.PayloadHash, x.Observations, x.Resolved, x.FirstSeenAtUtc, x.LastSeenAtUtc, x.ResolvedAtUtc }).ToArrayAsync(ct);
            Write(new { operation = options.Operation, dryRun = true, items = records });
            return options.Operation == "show" && records.Length == 0 ? 3 : 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine("Payment recovery failed: " + error.GetType().Name);
            return 1;
        }
    }
    private static void Write<T>(T value) => Console.WriteLine(JsonSerializer.Serialize(value, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    private sealed record Options(string Operation, Guid? Id, int Limit, bool Apply);
    private static Options? Parse(string[] args)
    {
        if (args.Length == 0 || args[0] is not ("list" or "show" or "replay")) return null;
        var operation = args[0]; Guid? id = null; var limit = 20; var apply = false;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 1; i < args.Length; i++)
        {
            var flag = args[i]; if (!seen.Add(flag)) return null;
            if (flag is "--apply" or "--dry-run")
            { if (operation != "replay") return null; apply = flag == "--apply"; }
            else if (flag == "--id")
            {
                if (operation == "list" || ++i >= args.Length || !Guid.TryParse(args[i], out var parsed) || parsed == Guid.Empty) return null;
                id = parsed;
            }
            else if (flag == "--limit")
            {
                if (operation != "list" || ++i >= args.Length || !int.TryParse(args[i], NumberStyles.None, CultureInfo.InvariantCulture, out limit) || limit is < 1 or > 100) return null;
            }
            else return null;
        }
        if (seen.Contains("--apply") && seen.Contains("--dry-run") || operation != "list" && id is null) return null;
        return new(operation, id, limit, apply);
    }
}
