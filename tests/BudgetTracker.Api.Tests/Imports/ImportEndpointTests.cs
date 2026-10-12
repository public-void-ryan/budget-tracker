using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using BudgetTracker.Api.Data.Entities;
using BudgetTracker.Api.Imports;
using BudgetTracker.Api.Tests.Auth;
using BudgetTracker.Api.Tests.Database;
using Microsoft.EntityFrameworkCore;

namespace BudgetTracker.Api.Tests.Imports;

public class ImportEndpointTests(PostgresFixture fixture) : ApiTest(fixture)
{
    private const string Header = "Date,Description,Amount\n";

    private async Task<(User User, string Token, Account Account)> SetUpAsync(bool invertSign = false, bool archived = false)
    {
        var (user, token) = await CreateUserWithSessionAsync();
        var account = new Account
        {
            UserId = user.Id,
            Name = "Everyday",
            InvertSign = invertSign,
            ArchivedAt = archived ? Clock.GetUtcNow() : null,
        };
        Db.Add(account);
        await Db.SaveChangesAsync();
        return (user, token, account);
    }

    private static string Csv(params string[] rows) => Header + string.Concat(rows.Select(r => r + "\n"));

    private async Task<HttpResponseMessage> ImportAsync(
        string? token, Guid accountId, string csv, string query = "", string contentType = "text/csv")
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/accounts/{accountId}/imports{query}")
        {
            Content = new StringContent(csv, Encoding.UTF8, contentType),
        };
        if (token is not null)
        {
            request.Headers.Authorization = new("Bearer", token);
        }

        return await Client.SendAsync(request);
    }

    private async Task<HttpResponseMessage> ImportBytesAsync(string token, Guid accountId, byte[] bytes, string contentType = "text/csv")
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/accounts/{accountId}/imports")
        {
            Content = new ByteArrayContent(bytes),
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        request.Headers.Authorization = new("Bearer", token);
        return await Client.SendAsync(request);
    }

    private async Task<JsonElement> SummaryAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ReadJsonAsync(response);
    }

    private static string[] Statuses(JsonElement summary) =>
        [.. summary.GetProperty("rows").EnumerateArray().Select(r => r.GetProperty("status").GetString()!)];

    private static (int Total, int New, int Duplicates, int Imported) Counts(JsonElement s) => (
        s.GetProperty("total").GetInt32(), s.GetProperty("new").GetInt32(),
        s.GetProperty("duplicates").GetInt32(), s.GetProperty("imported").GetInt32());

    // Preview and commit

    [Fact]
    public async Task A_preview_reports_every_row_and_writes_nothing()
    {
        var (_, token, account) = await SetUpAsync();

        var summary = await SummaryAsync(await ImportAsync(token, account.Id,
            Csv("2026-10-01,Coffee,-4.50", "2026-10-02,Salary,2500.00"), "?dryRun=true"));

        Assert.True(summary.GetProperty("dryRun").GetBoolean());
        Assert.Equal((2, 2, 0, 0), Counts(summary));
        Assert.Equal(["new", "new"], Statuses(summary));
        Assert.Equal(0, await Db.Transactions.CountAsync());
    }

    [Fact]
    public async Task A_commit_stores_the_new_rows_uncategorized_with_their_import_identity()
    {
        var (user, token, account) = await SetUpAsync();

        var summary = await SummaryAsync(await ImportAsync(token, account.Id,
            Csv("2026-10-01,Coffee,-4.50", "2026-10-02,Salary,2500.00")));

        Assert.False(summary.GetProperty("dryRun").GetBoolean());
        Assert.Equal((2, 2, 0, 2), Counts(summary));
        var stored = await Db.Transactions.AsNoTracking().OrderBy(t => t.PostedOn).ToListAsync();
        Assert.Equal(
            [("Coffee", -4.50m), ("Salary", 2500.00m)],
            stored.Select(t => (t.Description, t.Amount)).ToArray());
        Assert.All(stored, t =>
        {
            Assert.Equal((user.Id, account.Id, (Guid?)null, false), (t.UserId, t.AccountId, t.CategoryId, t.IsTransfer));
            Assert.Matches("^[0-9a-f]{64}$", t.ImportHash!.Trim());
            Assert.Equal((short)0, t.ImportSeq);
        });
    }

    [Fact]
    public async Task The_response_has_a_stable_shape()
    {
        var (_, token, account) = await SetUpAsync();

        var summary = await SummaryAsync(await ImportAsync(token, account.Id, Csv("2026-10-01,Coffee,-4.50"), "?dryRun=true"));

        Assert.Equal(
            ["dryRun", "duplicates", "imported", "new", "rows", "signInverted", "total"],
            summary.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).ToArray());
        var row = summary.GetProperty("rows")[0];
        Assert.Equal(
            ["amount", "description", "fileAmount", "postedOn", "row", "status"],
            row.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).ToArray());
        Assert.Equal("2026-10-01", row.GetProperty("postedOn").GetString());
    }

    [Fact]
    public async Task Imported_transactions_appear_in_the_transaction_list()
    {
        var (_, token, account) = await SetUpAsync();
        await ImportAsync(token, account.Id, Csv("2026-10-01,Coffee,-4.50", "2026-10-03,Tea,-3.00"));

        var page = await ReadJsonAsync(await SendAsync(HttpMethod.Get, "/transactions", token));

        Assert.Equal(
            ["Tea", "Coffee"],
            page.GetProperty("items").EnumerateArray().Select(t => t.GetProperty("description").GetString()!).ToArray());
    }

    [Fact]
    public async Task Descriptions_are_stored_trimmed_with_whitespace_collapsed()
    {
        var (_, token, account) = await SetUpAsync();

        await ImportAsync(token, account.Id, Csv("2026-10-01,\"  CARD   PURCHASE\n  COFFEE \",-4.50"));

        Assert.Equal("CARD PURCHASE COFFEE", (await Db.Transactions.AsNoTracking().SingleAsync()).Description);
    }

    // Deduplication

    [Fact]
    public async Task Importing_the_same_file_again_adds_nothing()
    {
        var (_, token, account) = await SetUpAsync();
        var file = Csv("2026-10-01,Coffee,-4.50", "2026-10-02,Salary,2500.00");
        await ImportAsync(token, account.Id, file);

        var again = await SummaryAsync(await ImportAsync(token, account.Id, file));

        Assert.Equal((2, 0, 2, 0), Counts(again));
        Assert.Equal(["duplicate", "duplicate"], Statuses(again));
        Assert.Equal(2, await Db.Transactions.CountAsync());
    }

    [Fact]
    public async Task Overlapping_statements_add_only_the_rows_they_do_not_share()
    {
        var (_, token, account) = await SetUpAsync();
        await ImportAsync(token, account.Id, Csv("2026-01-30,A,-10", "2026-01-31,B,-20", "2026-02-01,C,-30"));

        var second = await SummaryAsync(await ImportAsync(token, account.Id,
            Csv("2026-01-31,B,-20", "2026-02-01,C,-30", "2026-02-02,D,-40")));

        Assert.Equal((3, 1, 2, 1), Counts(second));
        Assert.Equal(["duplicate", "duplicate", "new"], Statuses(second));
        Assert.Equal(4, await Db.Transactions.CountAsync());
    }

    [Fact]
    public async Task Two_identical_rows_in_one_file_are_both_kept()
    {
        var (_, token, account) = await SetUpAsync();

        var summary = await SummaryAsync(await ImportAsync(token, account.Id,
            Csv("2026-10-01,Coffee,-4.50", "2026-10-01,Coffee,-4.50")));

        Assert.Equal((2, 2, 0, 2), Counts(summary));
        var stored = await Db.Transactions.AsNoTracking().OrderBy(t => t.ImportSeq).ToListAsync();
        Assert.Single(stored.Select(t => t.ImportHash).Distinct());
        Assert.Equal([(short)0, (short)1], stored.Select(t => t.ImportSeq!.Value).ToArray());
    }

    [Fact]
    public async Task A_later_export_with_one_more_identical_row_adds_exactly_one()
    {
        var (_, token, account) = await SetUpAsync();
        await ImportAsync(token, account.Id, Csv("2026-10-01,Coffee,-4.50", "2026-10-01,Coffee,-4.50"));

        var later = await SummaryAsync(await ImportAsync(token, account.Id,
            Csv("2026-10-01,Coffee,-4.50", "2026-10-01,Coffee,-4.50", "2026-10-01,Coffee,-4.50")));

        Assert.Equal(["duplicate", "duplicate", "new"], Statuses(later));
        Assert.Equal(3, await Db.Transactions.CountAsync());
    }

    [Fact]
    public async Task A_re_export_with_different_formatting_still_matches_what_is_stored()
    {
        var (_, token, account) = await SetUpAsync();
        await ImportAsync(token, account.Id, Csv("2026-10-02,STARBUCKS #12,-4.5", "2026-10-03,Rent,\"-1,000.00\""));

        var again = await SummaryAsync(await ImportAsync(token, account.Id,
            "Posted Date,Memo,Amount\n2026-10-02,  starbucks   #12 ,($4.50)\n2026-10-03,RENT,-1000\n"));

        Assert.Equal(["duplicate", "duplicate"], Statuses(again));
    }

    [Fact]
    public async Task A_manual_transaction_does_not_count_as_already_imported()
    {
        var (user, token, account) = await SetUpAsync();
        Db.Add(new Transaction
        {
            UserId = user.Id, AccountId = account.Id, PostedOn = new DateOnly(2026, 10, 1),
            Amount = -4.50m, Description = "Coffee",
        });
        await Db.SaveChangesAsync();

        var summary = await SummaryAsync(await ImportAsync(token, account.Id, Csv("2026-10-01,Coffee,-4.50")));

        Assert.Equal(["new"], Statuses(summary));
        Assert.Equal(2, await Db.Transactions.CountAsync());
    }

    [Fact]
    public async Task A_deleted_imported_transaction_comes_back_when_the_file_is_imported_again()
    {
        var (_, token, account) = await SetUpAsync();
        var file = Csv("2026-10-01,Coffee,-4.50", "2026-10-02,Tea,-3.00");
        await ImportAsync(token, account.Id, file);
        var coffee = await Db.Transactions.AsNoTracking().SingleAsync(t => t.Description == "Coffee");
        await SendAsync(HttpMethod.Delete, $"/transactions/{coffee.Id}", token);

        var again = await SummaryAsync(await ImportAsync(token, account.Id, file));

        Assert.Equal(["new", "duplicate"], Statuses(again));
        Assert.Equal(2, await Db.Transactions.CountAsync());
    }

    [Fact]
    public async Task The_same_file_can_be_imported_into_two_different_accounts()
    {
        var (user, token, first) = await SetUpAsync();
        var second = new Account { UserId = user.Id, Name = "Savings" };
        Db.Add(second);
        await Db.SaveChangesAsync();
        var file = Csv("2026-10-01,Coffee,-4.50");

        await ImportAsync(token, first.Id, file);
        var into = await SummaryAsync(await ImportAsync(token, second.Id, file));

        Assert.Equal(["new"], Statuses(into));
        Assert.Equal(2, await Db.Transactions.CountAsync());
    }

    [Fact]
    public async Task A_file_of_only_duplicates_is_a_normal_success_that_imports_nothing()
    {
        var (_, token, account) = await SetUpAsync();
        var file = Csv("2026-10-01,Coffee,-4.50");
        await ImportAsync(token, account.Id, file);

        var response = await ImportAsync(token, account.Id, file);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(0, (await ReadJsonAsync(response)).GetProperty("imported").GetInt32());
    }

    // The sign setting

    [Fact]
    public async Task An_account_that_inverts_signs_stores_the_flipped_amount_and_the_preview_shows_both()
    {
        var (_, token, account) = await SetUpAsync(invertSign: true);

        var preview = await SummaryAsync(await ImportAsync(token, account.Id, Csv("2026-10-01,Purchase,4.50"), "?dryRun=true"));
        await ImportAsync(token, account.Id, Csv("2026-10-01,Purchase,4.50"));

        Assert.True(preview.GetProperty("signInverted").GetBoolean());
        var row = preview.GetProperty("rows")[0];
        Assert.Equal(-4.50m, row.GetProperty("amount").GetDecimal());
        Assert.Equal(4.50m, row.GetProperty("fileAmount").GetDecimal());
        Assert.Equal(-4.50m, (await Db.Transactions.AsNoTracking().SingleAsync()).Amount);
    }

    [Fact]
    public async Task Changing_the_sign_setting_later_does_not_turn_a_re_import_into_new_transactions()
    {
        // The hash is of what the file said, not of what was stored, so toggling the setting
        // after the fact cannot make the same file look different.
        var (_, token, account) = await SetUpAsync(invertSign: true);
        var file = Csv("2026-10-01,Purchase,4.50");
        await ImportAsync(token, account.Id, file);
        await SendAsync(HttpMethod.Put, $"/accounts/{account.Id}", token, new { name = "Everyday", type = "Checking", invertSign = false });

        var again = await SummaryAsync(await ImportAsync(token, account.Id, file));

        Assert.Equal(["duplicate"], Statuses(again));
    }

    // Bad files

    [Fact]
    public async Task One_bad_row_rejects_the_whole_file_and_lists_every_problem()
    {
        var (_, token, account) = await SetUpAsync();

        var response = await ImportAsync(token, account.Id,
            Csv("2026-10-01,Good,-1", "bad-date,Bad,-2", "2026-10-03,,xx", "2026-10-04,Fine,-4"));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var problem = await ReadJsonAsync(response);
        Assert.Equal("import.invalid", problem.GetProperty("code").GetString());
        Assert.False(problem.GetProperty("errorsTruncated").GetBoolean());
        Assert.Equal(
            [(2, "date", "date.invalid"), (3, "description", "description.required"), (3, "amount", "amount.invalid")],
            problem.GetProperty("errors").EnumerateArray()
                .Select(e => (e.GetProperty("row").GetInt32(), e.GetProperty("field").GetString()!, e.GetProperty("code").GetString()!))
                .ToArray());
        Assert.Equal(0, await Db.Transactions.CountAsync());
    }

    [Fact]
    public async Task A_bad_file_is_rejected_the_same_way_in_a_preview()
    {
        var (_, token, account) = await SetUpAsync();

        var response = await ImportAsync(token, account.Id, Csv("nope,x,y"), "?dryRun=true");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task A_missing_column_is_reported_once_for_the_file()
    {
        var (_, token, account) = await SetUpAsync();

        var response = await ImportAsync(token, account.Id, "Foo,Bar\n1,2\n");

        var errors = (await ReadJsonAsync(response)).GetProperty("errors").EnumerateArray().ToList();
        Assert.Equal(3, errors.Count);
        Assert.All(errors, e => Assert.Equal(0, e.GetProperty("row").GetInt32()));
    }

    [Fact]
    public async Task Error_lists_are_capped_and_say_so()
    {
        var (_, token, account) = await SetUpAsync();

        var response = await ImportAsync(token, account.Id, Csv(Enumerable.Repeat("bad,x,1", 150).ToArray()));

        var problem = await ReadJsonAsync(response);
        Assert.Equal(100, problem.GetProperty("errors").GetArrayLength());
        Assert.True(problem.GetProperty("errorsTruncated").GetBoolean());
    }

    [Fact]
    public async Task An_empty_body_is_reported()
    {
        var (_, token, account) = await SetUpAsync();

        var response = await ImportAsync(token, account.Id, "");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("import.empty", (await ReadJsonAsync(response)).GetProperty("errors")[0].GetProperty("code").GetString());
    }

    [Fact]
    public async Task More_than_ten_thousand_rows_is_refused()
    {
        var (_, token, account) = await SetUpAsync();
        var rows = Enumerable.Range(0, 10_001).Select(i => $"2026-10-01,Row {i},-1").ToArray();

        var response = await ImportAsync(token, account.Id, Csv(rows));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(
            "import.too_many_rows",
            (await ReadJsonAsync(response)).GetProperty("errors")[0].GetProperty("code").GetString());
        Assert.Equal(0, await Db.Transactions.CountAsync());
    }

    [Fact]
    public async Task Ten_thousand_rows_import_in_one_go()
    {
        var (_, token, account) = await SetUpAsync();
        var rows = Enumerable.Range(0, 10_000).Select(i => $"2026-10-01,Row {i},-1").ToArray();

        var response = await ImportAsync(token, account.Id, Csv(rows));

        Assert.Equal(10_000, (await SummaryAsync(response)).GetProperty("imported").GetInt32());
        Assert.Equal(10_000, await Db.Transactions.CountAsync());
    }

    // Body limits and encoding

    [Fact]
    public async Task A_file_over_five_megabytes_is_refused_before_it_is_parsed()
    {
        var (_, token, account) = await SetUpAsync();
        var huge = new byte[ImportEndpoints.MaxBytes + 1];
        Array.Fill(huge, (byte)'a');

        var response = await ImportBytesAsync(token, account.Id, huge);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Equal("import.too_large", (await ReadJsonAsync(response)).GetProperty("code").GetString());
        Assert.Equal(0, await Db.Transactions.CountAsync());
    }

    [Fact]
    public async Task A_file_sent_as_a_stream_over_five_megabytes_is_refused()
    {
        // The in-memory test server may buffer a streamed body and supply a Content-Length, so
        // this checks the end-to-end behaviour only. The limit while reading, for bodies with no
        // trustworthy length, is tested directly in BoundedBodyTests.
        var (_, token, account) = await SetUpAsync();
        var huge = new byte[ImportEndpoints.MaxBytes + 1];
        Array.Fill(huge, (byte)'a');
        var request = new HttpRequestMessage(HttpMethod.Post, $"/accounts/{account.Id}/imports")
        {
            Content = new StreamContent(new MemoryStream(huge)),
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("text/csv");
        request.Headers.Authorization = new("Bearer", token);

        var response = await Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Equal(0, await Db.Transactions.CountAsync());
    }

    [Fact]
    public async Task A_file_that_is_not_utf8_is_refused_rather_than_stored_with_broken_accents()
    {
        var (_, token, account) = await SetUpAsync();
        var latin1 = Encoding.Latin1.GetBytes("Date,Description,Amount\n2026-10-01,Café,-4.50\n");

        var response = await ImportBytesAsync(token, account.Id, latin1);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("import.encoding", (await ReadJsonAsync(response)).GetProperty("code").GetString());
        Assert.Equal(0, await Db.Transactions.CountAsync());
    }

    [Fact]
    public async Task Utf8_with_a_byte_order_mark_and_real_accents_is_stored_correctly()
    {
        var (_, token, account) = await SetUpAsync();
        var bytes = Encoding.UTF8.GetPreamble()
            .Concat(Encoding.UTF8.GetBytes("Date,Description,Amount\n2026-10-01,Café 日本,-4.50\n"))
            .ToArray();

        var response = await ImportBytesAsync(token, account.Id, bytes);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Café 日本", (await Db.Transactions.AsNoTracking().SingleAsync()).Description);
    }

    [Fact]
    public async Task Plain_text_is_accepted_as_a_content_type_but_json_is_not()
    {
        var (_, token, account) = await SetUpAsync();

        var plain = await ImportAsync(token, account.Id, Csv("2026-10-01,Coffee,-4.50"), contentType: "text/plain");
        var json = await ImportAsync(token, account.Id, Csv("2026-10-02,Tea,-3.00"), contentType: "application/json");

        Assert.Equal(HttpStatusCode.OK, plain.StatusCode);
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, json.StatusCode);
        Assert.Equal(1, await Db.Transactions.CountAsync());
    }

    // Query options

    [Fact]
    public async Task A_named_date_layout_is_applied()
    {
        var (_, token, account) = await SetUpAsync();

        await ImportAsync(token, account.Id, Csv("03/04/2026,Coffee,-4.50"), "?dateFormat=eu");

        Assert.Equal(new DateOnly(2026, 4, 3), (await Db.Transactions.AsNoTracking().SingleAsync()).PostedOn);
    }

    [Fact]
    public async Task An_unknown_date_layout_is_a_400()
    {
        var (_, token, account) = await SetUpAsync();

        var response = await ImportAsync(token, account.Id, Csv("2026-10-01,Coffee,-4.50"), "?dateFormat=yyyy-dd-MM");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(
            "dateFormat.invalid",
            (await ReadJsonAsync(response)).GetProperty("codes").GetProperty("dateFormat")[0].GetString());
    }

    [Fact]
    public async Task Explicit_columns_and_split_debit_and_credit_columns_work_through_the_query()
    {
        var (_, token, account) = await SetUpAsync();
        var csv = "Booked,Text,Out,In\n2026-10-01,Coffee,4.50,\n2026-10-02,Salary,,2500.00\n";

        var response = await ImportAsync(token, account.Id, csv,
            "?dateColumn=Booked&descriptionColumn=Text&debitColumn=Out&creditColumn=In");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(
            [-4.50m, 2500.00m],
            (await Db.Transactions.AsNoTracking().OrderBy(t => t.PostedOn).Select(t => t.Amount).ToListAsync()).ToArray());
    }

    [Fact]
    public async Task Naming_a_column_the_file_does_not_have_is_reported_as_a_file_problem()
    {
        var (_, token, account) = await SetUpAsync();

        var response = await ImportAsync(token, account.Id, Csv("2026-10-01,Coffee,-4.50"), "?amountColumn=Total");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("header.missing", (await ReadJsonAsync(response)).GetProperty("errors")[0].GetProperty("code").GetString());
    }

    // Accounts, ownership, authentication

    [Fact]
    public async Task An_archived_account_refuses_imports_and_previews()
    {
        var (_, token, account) = await SetUpAsync(archived: true);

        var commit = await ImportAsync(token, account.Id, Csv("2026-10-01,Coffee,-4.50"));
        var preview = await ImportAsync(token, account.Id, Csv("2026-10-01,Coffee,-4.50"), "?dryRun=true");

        Assert.Equal(HttpStatusCode.Conflict, commit.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, preview.StatusCode);
        Assert.Equal("account.archived", (await ReadJsonAsync(commit)).GetProperty("code").GetString());
        Assert.Equal(0, await Db.Transactions.CountAsync());
    }

    [Fact]
    public async Task Another_users_account_or_a_missing_one_is_a_404_and_nothing_is_written()
    {
        var (_, alice, _) = await SetUpAsync();
        var (_, _, bobsAccount) = await SetUpAsync();

        var foreign = await ImportAsync(alice, bobsAccount.Id, Csv("2026-10-01,Coffee,-4.50"));
        var missing = await ImportAsync(alice, Guid.NewGuid(), Csv("2026-10-01,Coffee,-4.50"));

        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal("not_found", (await ReadJsonAsync(foreign)).GetProperty("code").GetString());
        Assert.Equal(0, await Db.Transactions.CountAsync());
    }

    [Fact]
    public async Task An_import_needs_a_session()
    {
        var (_, _, account) = await SetUpAsync();

        var response = await ImportAsync(token: null, account.Id, Csv("2026-10-01,Coffee,-4.50"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(0, await Db.Transactions.CountAsync());
    }

    // Concurrency

    [Fact]
    public async Task Simultaneous_imports_of_the_same_file_store_each_row_exactly_once()
    {
        // Without the account lock each of these would see the rows as missing and try to insert
        // them, and all but one would fail on the unique key or double up.
        var (_, token, account) = await SetUpAsync();
        var file = Csv(Enumerable.Range(0, 50).Select(i => $"2026-10-01,Row {i % 40},-{1 + i % 7}").ToArray());

        var responses = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => ImportAsync(token, account.Id, file)));

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        var imported = 0;
        foreach (var response in responses)
        {
            imported += (await ReadJsonAsync(response)).GetProperty("imported").GetInt32();
        }

        Assert.Equal(50, imported);
        Assert.Equal(50, await Db.Transactions.CountAsync());
        Assert.Equal(50, await Db.Transactions.Select(t => new { t.ImportHash, t.ImportSeq }).Distinct().CountAsync());
    }

    [Fact]
    public async Task Simultaneous_imports_of_overlapping_files_together_store_the_union_once()
    {
        var (_, token, account) = await SetUpAsync();
        string File(int from, int to) => Csv(Enumerable.Range(from, to - from).Select(i => $"2026-10-01,Row {i},-1").ToArray());

        var responses = await Task.WhenAll(
            ImportAsync(token, account.Id, File(0, 30)),
            ImportAsync(token, account.Id, File(15, 45)),
            ImportAsync(token, account.Id, File(30, 60)));

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        Assert.Equal(60, await Db.Transactions.CountAsync());
    }
}
