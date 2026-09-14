using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using WorkIqProfileChat.Api.Options;
using WorkIqProfileChat.Api.Security;

namespace WorkIqProfileChat.Api.Tests;

public sealed class EasyAuthUserContextTests
{
    private const string TenantId = "11111111-1111-1111-1111-111111111111";
    private const string RequiredRole = "ProfileChat.User";
    private readonly EasyAuthUserContext _context = new(
        Microsoft.Extensions.Options.Options.Create(new AuthenticationOptions
        {
            TenantId = TenantId,
            ApiClientId = "33333333-3333-3333-3333-333333333333",
            RequiredRole = RequiredRole
        }));

    [Fact]
    public void RequireAllowedUser_ReturnsUserForExpectedTenantAndGroup()
    {
        TestHttpRequestData request = RequestWithPrincipal(
            new EasyAuthClaim("tid", TenantId),
            new EasyAuthClaim("oid", "44444444-4444-4444-4444-444444444444"),
            new EasyAuthClaim("roles", RequiredRole));

        AuthenticatedUser user = _context.RequireAllowedUser(request);

        Assert.Equal(TenantId, user.TenantId);
        Assert.Equal("44444444-4444-4444-4444-444444444444", user.ObjectId);
    }

    [Fact]
    public void RequireAllowedUser_RejectsUserOutsideAllowlist()
    {
        TestHttpRequestData request = RequestWithPrincipal(
            new EasyAuthClaim("tid", TenantId),
            new EasyAuthClaim("oid", "44444444-4444-4444-4444-444444444444"));

        UnauthorizedAccessException error = Assert.Throws<UnauthorizedAccessException>(
            () => _context.RequireAllowedUser(request));

        Assert.Contains("not assigned", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RequireUserAssertion_ReadsBearerToken()
    {
        TestHttpRequestData request = new();
        request.Headers.Add("Authorization", "Bearer delegated-token");

        Assert.Equal("delegated-token", _context.RequireUserAssertion(request));
    }

    [Fact]
    public void ParsePrincipal_RejectsMalformedHeader()
    {
        TestHttpRequestData request = new();
        request.Headers.Add("x-ms-client-principal", "not-base64");

        Assert.Throws<UnauthorizedAccessException>(
            () => EasyAuthUserContext.ParsePrincipal(request));
    }

    private static TestHttpRequestData RequestWithPrincipal(params EasyAuthClaim[] claims)
    {
        EasyAuthPrincipal principal = new("aad", "name", "roles", claims);
        string encoded = Convert.ToBase64String(
            Encoding.UTF8.GetBytes(JsonSerializer.Serialize(principal)));
        TestHttpRequestData request = new();
        request.Headers.Add("x-ms-client-principal", encoded);
        return request;
    }

    private sealed class TestHttpRequestData : HttpRequestData
    {
        public TestHttpRequestData() : base(new TestFunctionContext())
        {
        }

        public override Stream Body { get; } = new MemoryStream();
        public override HttpHeadersCollection Headers { get; } = [];
        public override IReadOnlyCollection<IHttpCookie> Cookies { get; } = [];
        public override Uri Url { get; } = new("https://localhost/api/chat");
        public override IEnumerable<ClaimsIdentity> Identities { get; } = [];
        public override string Method { get; } = "POST";

        public override HttpResponseData CreateResponse() =>
            new TestHttpResponseData(FunctionContext);
    }

    private sealed class TestHttpResponseData(FunctionContext context)
        : HttpResponseData(context)
    {
        public override HttpStatusCode StatusCode { get; set; }
        public override HttpHeadersCollection Headers { get; set; } = [];
        public override Stream Body { get; set; } = new MemoryStream();
        public override HttpCookies Cookies { get; } = null!;
    }

    private sealed class TestFunctionContext : FunctionContext
    {
        public override string InvocationId { get; } = Guid.NewGuid().ToString();
        public override string FunctionId { get; } = "test";
        public override TraceContext TraceContext { get; } = null!;
        public override BindingContext BindingContext { get; } = null!;
        public override RetryContext RetryContext { get; } = null!;
        public override IServiceProvider InstanceServices { get; set; } =
            new ServiceCollection().BuildServiceProvider();
        public override FunctionDefinition FunctionDefinition { get; } = null!;
        public override IDictionary<object, object> Items { get; set; } =
            new Dictionary<object, object>();
        public override IInvocationFeatures Features { get; } = null!;
        public override CancellationToken CancellationToken { get; } = CancellationToken.None;
    }
}
