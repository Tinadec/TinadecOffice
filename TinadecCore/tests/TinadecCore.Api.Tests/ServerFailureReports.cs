using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using TinadecCore.AspNetCore;
using Xunit;

namespace TinadecCore.Api.Tests;

/// <summary>
/// Turns a Core 5xx into the sentence the handler could not print.
/// <para>
/// The exception handler maps an unmapped exception to <c>internal_error</c> with a fixed detail, and
/// the response is the only thing a test had to go on — so the whole family of high-load reds
/// ("interactions 500", "approvals poll 500") arrived with no cause attached, and the only clue was
/// a stack frame. Core keeps the cause in <see cref="ServerFailureJournal"/>; a hosted factory can
/// read it back through DI.
/// </para>
/// </summary>
internal static class ServerFailureReports
{
    /// <summary>
    /// The most recent 5xx this host produced, or an explicit statement that there was none — which is
    /// itself evidence, because it means the status came from something other than the exception
    /// handler and the caller should look elsewhere rather than assume a database race.
    /// </summary>
    public static string LastFailure(this WebApplicationFactory<Program> factory)
    {
        var last = factory.Services.GetService<ServerFailureJournal>()?.Recent().LastOrDefault();
        return last is null
            ? "the handler recorded no 5xx for this host, so this status did not come from a thrown exception"
            : last.Describe();
    }

    /// <summary>
    /// The one line a fixer reads: route, machine code, thrown type, and the innermost message.
    /// The wrapper is dropped when it is the whole chain, because repeating it reads like two facts.
    /// </summary>
    public static string Describe(this ServerFailure failure)
    {
        var root = failure.RootType is null || failure.RootType == failure.ExceptionType
            ? string.Empty
            : $" <- {failure.RootType}: {failure.RootMessage}";
        return $"{failure.Method} {failure.Path} [{failure.Code}] {failure.ExceptionType}: {failure.ExceptionMessage}{root}";
    }

    /// <summary>
    /// Asserts a Core response status, and on mismatch prints both the problem document and the cause.
    /// <para>
    /// A bare <c>Assert.Equal</c> on a status compares two enum values: the red says "Expected Created,
    /// Actual InternalServerError" and throws away the response body it had already fetched, which is
    /// where the problem document (and now the journal) lives. The stream helpers are shared by every
    /// full-duplex test, so this is the one place the sentence gets built.
    /// </para>
    /// </summary>
    public static async Task AssertStatusAsync(
        this WebApplicationFactory<Program> factory,
        HttpResponseMessage response,
        HttpStatusCode expected,
        string what)
    {
        if (response.StatusCode == expected) return;
        var body = await response.Content.ReadAsStringAsync();
        Assert.Fail($"{what} returned {(int)response.StatusCode} {response.StatusCode}: {body} | server cause: {factory.LastFailure()}");
    }
}
