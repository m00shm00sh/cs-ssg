using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Logging;
using Xunit.Abstractions;
using ZiggyCreatures.Caching.Fusion;

using CsSsg.Src.Db;
using CsSsg.Src.Media;
using MObject = CsSsg.Src.Media.Object;
using static CsSsg.Src.Media.RoutingExtensions;
using static CsSsg.Src.Post.IManageCommand;
using CsSsg.Src.SharedTypes;
using CsSsg.Src.User;
using static CsSsg.Src.User.RoutingExtensions;

using CsSsg.Test.Db;
using CsSsg.Test.Post;
using CsSsg.Test.StreamSupport;

namespace CsSsg.Test.Media;
public class ApiTests : IClassFixture<PostgresFixture>
{
#region scaffolding
    private readonly Func<AppDbContext> _contextFactory;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<ApiTests> _logger;
    private readonly IFusionCache _cache = new FusionCache(new FusionCacheOptions());
    // these two must be static for adequate sharing as xunit seems to be producing multiple instances
    private static int _userCounter;
    private static int _fileCounter;
    
    const string IMPOSSIBLE_SLUG = "-"; // this slug can never appear because it is invalid

    public ApiTests(PostgresFixture fixture, ITestOutputHelper outputHelper)
    {
        _contextFactory = () => new AppDbContext(fixture.DbContextOptions);
        _loggerFactory = LoggerFactory.Create(builder => builder.AddXUnit(outputHelper));
        _logger = _loggerFactory.CreateLogger<ApiTests>();
    }
    
    private static int _nextUserId =>  Interlocked.Increment(ref _userCounter);
    private static int _nextFileId =>  Interlocked.Increment(ref _fileCounter);

    private async Task<(string, Guid)> _nextUserAsync(AppDbContext continueContext, CancellationToken token)
    {
        var next = _nextUserId;
        var nextUserId = $"{next:00}";
        _logger.LogInformation("Create user {nextUserId}", nextUserId);
        var user = new Request(Email: $"{nextUserId}@test!media", Password: $"test{nextUserId}");
        var (signupResult, signupUid) = await DoPostUserSignupActionAsync(continueContext, user, token);
        Assert.NotNull(signupResult as RedirectHttpResult);
        return (user.Email, signupUid);
    }
#endregion
#region Create media tests
    [Fact]
    public async Task TestCreateMedia()
    {
        await using var dbContext = _contextFactory();
        var token = CancellationToken.None;
        var rLogger = _loggerFactory.CreateLogger<Routing>();
        var (_, uid) = await _nextUserAsync(dbContext, token);

        _logger.LogInformation("Create media");
        await using var stream = new RepeatingByteStream(1, 1);
        var file = new MObject("a/a", stream);
        var name = $"smiley{_nextFileId}.png";
        var result = await DoSubmitMediaCreationAsync(name, file, uid,
            dbContext, _cache, rLogger, token);
        result.RequireInsertSuccess(_logger);
    }
    
    [Fact]
    public async Task TestCreateMedia_EnforcesSizeLimit()
    {
        await using var dbContext = _contextFactory();
        var token = CancellationToken.None;
        var rLogger = _loggerFactory.CreateLogger<Routing>();
        var (_, uid) = await _nextUserAsync(dbContext, token);

        var len = await dbContext.GetUserMediaUploadSizeLimitAsync(uid, token);
        _logger.LogInformation("Create media");
        await using var stream = new RepeatingByteStream(1, len + 1);
        var file = new MObject("a/a", stream);
        var name = $"smiley{_nextFileId}.png";
        var result = await DoSubmitMediaCreationAsync(name, file, uid,
            dbContext, _cache, rLogger, token);
        result.Match(
            inserted => Assert.Fail($"expected failCode=TooLong but got inserted={inserted}"),
            failCode => Assert.Equal(Failure.TooLong, failCode)
        );
    }
    
    [Fact]
    public async Task TestCreateMedia_ResolvesInsertDuplicates()
    {
        await using var dbContext = _contextFactory();
        var token = CancellationToken.None;
        var rLogger = _loggerFactory.CreateLogger<Routing>();
        var (_, uid) = await _nextUserAsync(dbContext, token);

        _logger.LogInformation("Create media");
        await using var stream = new RepeatingByteStream(1, 1);
        var file = new MObject("a/a", stream);
        var name = $"smiley{_nextFileId}.png";
        var result = await DoSubmitMediaCreationAsync(name, file, uid, 
            dbContext, _cache, rLogger, token);
        result.RequireInsertSuccess(_logger);
        stream._position = 0;
        result = await DoSubmitMediaCreationAsync(name, file, uid, dbContext, _cache, rLogger, token);
        result.RequireInsertSuccess(_logger);
    }
    
    [Fact]
    public async Task TestCreateMedia_ThenFetchIt()
    {
        await using var dbContext = _contextFactory();
        var token = CancellationToken.None;
        var rLogger = _loggerFactory.CreateLogger<Routing>();
        var (_, uid) = await _nextUserAsync(dbContext, token);

        _logger.LogInformation("Create media");
        await using var stream = new RepeatingByteStream(1, 1);
        var cType = "xxx/aaa";
        var file = new MObject(cType, stream);
        var name = $"smiley{_nextFileId}.png";
        var result = await DoSubmitMediaCreationAsync(name, file, uid,
            dbContext, _cache, rLogger, token);
        var inserted = result.RequireInsertSuccess(_logger);
        stream._position = 0;
        var fileData = await stream.SaveToArrayAsync(token);
        
        _logger.LogInformation("Fetch entry");
        var fetchResult = (FileStreamHttpResult)await DoGetMediaForNameAsync(inserted, uid, dbContext, _cache, token);
        var gotData = await fetchResult.FileStream.SaveToArrayAsync(token);
        var gotCType = fetchResult.ContentType;
        Assert.Equal(fileData, gotData);
        Assert.Equal(cType, gotCType);
    }
    
    [Fact]
    public async Task TestCreateMedia_ThenFetchListing()
    {
        await using var dbContext = _contextFactory();
        var token = CancellationToken.None;
        var rLogger = _loggerFactory.CreateLogger<Routing>();
        var (email, uid) = await _nextUserAsync(dbContext, token);

        _logger.LogInformation("Create media");
        await using var stream = new RepeatingByteStream(1, 1);
        var cType = "xxx/aaa";
        var file = new MObject(cType, stream);
        var name = $"smiley{_nextFileId}.png";
        var result = await DoSubmitMediaCreationAsync(name, file, uid,
            dbContext, _cache, rLogger, token);
        var inserted = result.RequireInsertSuccess(_logger);
        
        _logger.LogInformation("Fetch listing");
        var utcNow = DateTime.UtcNow;
        var entryItr = await DoGetAllAvailableMediaEntriesForUserAsync(uid, 2, utcNow,
            dbContext, _cache, token);
        var entries = entryItr.ToList();
        var entry = entries.Single(e => e.Slug == inserted);
        Assert.Equal(cType, entry.ContentType);
        Assert.Equal(inserted, entry.Slug);
        Assert.False(entry.IsPublic);
    }
    
    public static IList<object[]> InvalidFileSlugs =
    [
        ["--", Failure.Conflict], // resolves to ""
        ["-", Failure.Conflict], // resolves to ""
        ["", Failure.Conflict],
        [new string('a', 255), Failure.TooLong] // the current TITLE_MAXLEN is 250
    ];
    
    [Theory]
    [MemberData(nameof(InvalidFileSlugs))]
    public async Task TestCreateMedia_FailsForInvalidFileSlug(string fileSlug, object /* Failure */ expFailCode)
    {
        await using var dbContext = _contextFactory();
        var token = CancellationToken.None;
        var rLogger = _loggerFactory.CreateLogger<Routing>();
        var (email, uid) = await _nextUserAsync(dbContext, token);

        _logger.LogInformation("Create media");
        await using var stream = new RepeatingByteStream(1, 1);
        var cType = "xxx/yyy";
        var file = new MObject(cType, stream);
        var result = await DoSubmitMediaCreationAsync(fileSlug, file, uid,
            dbContext, _cache, rLogger, token);
        result.Match(
            inserted => Assert.Fail($"expected failCode=Conflict but got inserted={inserted}"),
            failCode => Assert.Equal(expFailCode, failCode)
        );
    }
    
    public static IList<object[]> InvalidContentTypes =
    [
        ["", Failure.Conflict],
        [new string('a', 256), Failure.TooLong] // the current CTYPE_MAXLEN is 255
    ];
    
    [Theory]
    [MemberData(nameof(InvalidContentTypes))]
    public async Task TestCreateMedia_FailsForEmptyContentType(string cType, object /* Failure*/ expFailCode)
    {
        await using var dbContext = _contextFactory();
        var token = CancellationToken.None;
        var rLogger = _loggerFactory.CreateLogger<Routing>();
        var (email, uid) = await _nextUserAsync(dbContext, token);

        _logger.LogInformation("Create media");
        await using var stream = new RepeatingByteStream(1, 1);
        var file = new MObject(cType, stream);
        var result = await DoSubmitMediaCreationAsync(cType, file, uid,
            dbContext, _cache, rLogger, token);
        result.Match(
            inserted => Assert.Fail($"expected failCode={expFailCode} but got inserted={inserted}"),
            failCode => Assert.Equal(expFailCode, failCode)
        );
    }
    
    [Fact]
    public async Task TestCreateMedia_FailsForInvalidUser()
    {
        await using var dbContext = _contextFactory();
        var token = CancellationToken.None;
        var rLogger = _loggerFactory.CreateLogger<Routing>();

        var badUid = Guid.Empty;
        
        await using var stream = new RepeatingByteStream(1, 1);
        var file = new MObject("a/a", stream);
        var result = await DoSubmitMediaCreationAsync("a", file, badUid,
            dbContext, _cache, rLogger, token);
        result.Match(
            inserted => Assert.Fail($"expected failCode=NotPermitted but got inserted={inserted}"),
            failCode => Assert.Equal(Failure.NotPermitted, failCode)
        );
    }
    
    [Fact]
    public async Task TestCreateMedia_ThenFetchEntry_FailsForPublic()
    {
        await using var dbContext = _contextFactory();
        var token = CancellationToken.None;
        var rLogger = _loggerFactory.CreateLogger<Routing>();
        var (_, uid) = await _nextUserAsync(dbContext, token);

        _logger.LogInformation("Create media");
        await using var stream = new RepeatingByteStream(1, 1);
        var cType = "image/png";
        var file = new MObject(cType, stream);
        var name = $"smiley{_nextFileId}.png";
        var result = await DoSubmitMediaCreationAsync(name, file, uid,
            dbContext, _cache, rLogger, token);
        var inserted = result.RequireInsertSuccess(_logger);
        
        _logger.LogInformation("Attempt to fetch publicly");
        var entry = await DoGetMediaForNameAsync(inserted, null, dbContext, _cache, token);
        Assert.IsType<ForbidHttpResult>(entry);
    }
#endregion
#region Fetch media tests
    [Fact]
    public async Task TestFetchEntry_FailsForMissing()
    {
        await using var dbContext = _contextFactory();
        var token = CancellationToken.None;
        var entry = await DoGetMediaForNameAsync(IMPOSSIBLE_SLUG, null, dbContext, _cache, token);
        Assert.IsType<NotFound>(entry);
    }
#endregion
#region Update media tests
    [Fact]
    public async Task TestCreateMedia_ThenUpdateIt()
    {
        await using var dbContext = _contextFactory();
        var token = CancellationToken.None;
        var rLogger = _loggerFactory.CreateLogger<Routing>();
        var (_, uid) = await _nextUserAsync(dbContext, token);

        _logger.LogInformation("Create media");
        await using var stream = new RepeatingByteStream(1, 1);
        var cType = "xxx/aaa";
        var file = new MObject(cType, stream);
        var name = $"smiley{_nextFileId}.png";
        var result = await DoSubmitMediaCreationAsync(name, file, uid,
            dbContext, _cache, rLogger, token);
        var slug = result.RequireInsertSuccess(_logger);
            
        _logger.LogInformation("Update media");
        await using var stream2 = new RepeatingByteStream(2, 2);
        var cType2 = "xxx/bbb";
        var newFile = new MObject(cType2, stream2);
        var updateResult = await DoSubmitMediaEditForNameAsync(slug, uid, newFile, false,
            dbContext, _cache, rLogger, token);
        updateResult.IfSome(failCode => Assert.Fail($"update failed: {failCode}"));
    }
    
    [Fact]
    public async Task TestCreateMedia_ThenUpdateIt_EnforcesSizeLimit()
    {
        await using var dbContext = _contextFactory();
        var token = CancellationToken.None;
        var rLogger = _loggerFactory.CreateLogger<Routing>();
        var (_, uid) = await _nextUserAsync(dbContext, token);

        _logger.LogInformation("Create media");
        await using var stream = new RepeatingByteStream(1, 1);
        var cType = "xxx/aaa";
        var file = new MObject(cType, stream);
        var name = $"smiley{_nextFileId}.png";
        var result = await DoSubmitMediaCreationAsync(name, file, uid,
            dbContext, _cache, rLogger, token);
        var slug = result.RequireInsertSuccess(_logger);
        
        _logger.LogInformation("Update media");
        var len = await dbContext.GetUserMediaUploadSizeLimitAsync(uid, token);
        await using var stream2 = new RepeatingByteStream(2, len + 1);
        var cType2 = "xxx/bbb";
        var newFile = new MObject(cType2, stream2);
        var updateResult = await DoSubmitMediaEditForNameAsync(slug, uid, newFile, false,
            dbContext, _cache, rLogger, token);
        updateResult.Match(
            failCode => Assert.Equal(Failure.TooLong, failCode),
            () => Assert.Fail($"expected failCode=TooLong but got success"));
    }
    
    [Fact]
    public async Task TestCreateMedia_ThenUpdateIt_ThenFetchIt()
    {
        await using var dbContext = _contextFactory();
        var token = CancellationToken.None;
        var rLogger = _loggerFactory.CreateLogger<Routing>();
        var (_, uid) = await _nextUserAsync(dbContext, token);

        _logger.LogInformation("Create media");
        await using var stream = new RepeatingByteStream(1, 1);
        var cType = "xxx/aaa";
        var file = new MObject(cType, stream);
        var name = $"smiley{_nextFileId}.png";
        var result = await DoSubmitMediaCreationAsync(name, file, uid,
            dbContext, _cache, rLogger, token);
        var slug = result.RequireInsertSuccess(_logger);
        
        _logger.LogInformation("Update media");
        await using var stream2 = new RepeatingByteStream(2, 2);
        cType = "xxx/bbb";
        var newFile = new MObject(cType, stream2);
        var updateResult = await DoSubmitMediaEditForNameAsync(slug, uid, newFile, false,
            dbContext, _cache, rLogger, token);
        updateResult.IfSome(failCode => Assert.Fail($"update failed: {failCode}"));
        
        _logger.LogInformation("Fetch entry");
        var fetchResult = (FileStreamHttpResult)await DoGetMediaForNameAsync(slug, uid, dbContext, _cache, token);
        var gotData = await fetchResult.FileStream.SaveToArrayAsync(token);
        var gotCType = fetchResult.ContentType;
        var expData = await (new RepeatingByteStream(2, 2)).SaveToArrayAsync(token);
        Assert.Equal(expData, gotData);
        Assert.Equal(cType, gotCType);
    }
    
    [Fact]
    public async Task TestCreateMedia_ThenUpdateIt_FailsForAnonymousUid()
    {
        await using var dbContext = _contextFactory();
        var token = CancellationToken.None;
        var rLogger = _loggerFactory.CreateLogger<Routing>();
        var (_, uid) = await _nextUserAsync(dbContext, token);

        _logger.LogInformation("Create media");
        await using var stream = new RepeatingByteStream(1, 1);
        var cType = "xxx/aaa";
        var file = new MObject(cType, stream);
        var name = $"smiley{_nextFileId}.png";
        var result = await DoSubmitMediaCreationAsync(name, file, uid,
            dbContext, _cache, rLogger, token);
        var slug = result.RequireInsertSuccess(_logger);
        
        _logger.LogInformation("Update media");
        await using var stream2 = new RepeatingByteStream(2, 2);
        cType = "xxx/bbb";
        var newFile = new MObject(cType, stream2);
        var updateResult = await DoSubmitMediaEditForNameAsync(slug, Guid.Empty, newFile, false,
            dbContext, _cache, rLogger, token);
        updateResult.IfNone(() => Assert.Fail("expected failed update"));
    }
    
    [Fact]
    public async Task TestUpdateMedia_FailsForNonexistent()
    {
        await using var dbContext = _contextFactory();
        var token = CancellationToken.None;
        var rLogger = _loggerFactory.CreateLogger<Routing>();

        _logger.LogInformation("Update media");
        await using var stream = new RepeatingByteStream(2, 2);
        var cType = "xxx/bbb";
        var newFile = new MObject(cType, stream);
        var updateResult = await DoSubmitMediaEditForNameAsync(IMPOSSIBLE_SLUG, Guid.Empty, newFile, false,
            dbContext, _cache, rLogger, token);
        
        updateResult.Match(
            failCode => Assert.Equal(Failure.NotFound, failCode),
            () => Assert.Fail("failed to error")
        );
    }
#endregion
#region Fetch media manage page tests
    [Fact]
    public async Task TestCreateMedia_ThenFetchItsManagePage_PropagatingSuppliedPermissionValues()
    {
        await using var dbContext = _contextFactory();
        var token = CancellationToken.None;
        var rLogger = _loggerFactory.CreateLogger<Routing>();
        var (_, uid) = await _nextUserAsync(dbContext, token);
        
        _logger.LogInformation("Create media");
        await using var stream = new RepeatingByteStream(1, 1);
        var cType = "xxx/aaa";
        var file = new MObject(cType, stream);
        var name = $"smiley{_nextFileId}.png";
        var result = await DoSubmitMediaCreationAsync(name, file, uid,
            dbContext, _cache, rLogger, token);
        var slug = result.RequireInsertSuccess(_logger);

        _logger.LogInformation("Fetch manage");
        var perms = new Permissions
        {
            Public = true // this contradicts defaults but is useful for verifying propagation
        };
        var mResult = await DoGetManagePageForNameAndPermissionAsync(slug, uid, perms, dbContext, _cache, token);
        Assert.Equal(stream._length, mResult.Size);
        Assert.Equal(cType, mResult.ContentType);
        Assert.Equal(perms, mResult.Permissions);
    }
    
    [Fact]
    public async Task TestManagePage_FailsForMissing()
    {
        await using var dbContext = _contextFactory();
        var token = CancellationToken.None;

        var perms = new Permissions();
        var message = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await DoGetManagePageForNameAndPermissionAsync(IMPOSSIBLE_SLUG, Guid.Empty, perms, dbContext, _cache, token);
        });
        Assert.Contains("missing entry", message.Message);
    }
#endregion
#region Rename media tests
    [Fact]
    public async Task TestCreateMedia_ThenRenameIt()
    {
        await using var dbContext = _contextFactory();
        var token = CancellationToken.None;
        var rLogger = _loggerFactory.CreateLogger<Routing>();
        var (_, uid) = await _nextUserAsync(dbContext, token);

        _logger.LogInformation("Create media");
        await using var stream = new RepeatingByteStream(1, 1);
        var cType = "xxx/aaa";
        var file = new MObject(cType, stream);
        var name = $"smiley{_nextFileId}.png";
        var result = await DoSubmitMediaCreationAsync(name, file, uid,
            dbContext, _cache, rLogger, token);
        var slug = result.RequireInsertSuccess(_logger);
            
        _logger.LogInformation("Rename entry");
        var newSlug = $"smileyX{_nextFileId}.png";
        var command = new Rename(newSlug);
        var manageResult = await DoSubmitRenameForNameAsync(slug, uid, command, dbContext, _cache, rLogger, token);
        manageResult.Match(
            newName => _logger.LogInformation("rename success: {newName}", newName),
            failCode => Assert.Fail($"rename failed: {failCode}"));
    }
    
    [Fact]
    public async Task TestCreatePost_ThenRename_ThenFetchIt_FailsForOldName()
    {
        await using var dbContext = _contextFactory();
        var token = CancellationToken.None;
        var rLogger = _loggerFactory.CreateLogger<Routing>();
        var (_, uid) = await _nextUserAsync(dbContext, token);

        _logger.LogInformation("Create media");
        await using var stream = new RepeatingByteStream(1, 1);
        var cType = "xxx/aaa";
        var file = new MObject(cType, stream);
        var name = $"smiley{_nextFileId}.png";
        var result = await DoSubmitMediaCreationAsync(name, file, uid,
            dbContext, _cache, rLogger, token);
        var slug = result.RequireInsertSuccess(_logger);
            
        _logger.LogInformation("Rename media");
        var newSlug = $"smileyX{_nextFileId}.png";
        var command = new Rename(newSlug);
        var manageResult = await DoSubmitRenameForNameAsync(slug, uid, command, dbContext, _cache, rLogger, token);
        manageResult.Match(
            newName => _logger.LogInformation("rename success: {newName}", newName),
            failCode => Assert.Fail($"rename failed: {failCode}"));
       
        _logger.LogInformation("Attempt to fetch by old name");
        var fetchResult = await DoGetMediaForNameAsync(slug, uid, dbContext, _cache, token);
        Assert.Throws<InvalidCastException>(() => (FileStreamHttpResult)fetchResult);
    }
    
    [Fact]
    public async Task TestCreateMedia_ThenRenameIt_ThenFetchIt()
    {
        await using var dbContext = _contextFactory();
        var token = CancellationToken.None;
        var rLogger = _loggerFactory.CreateLogger<Routing>();
        var (_, uid) = await _nextUserAsync(dbContext, token);

        _logger.LogInformation("Create media");
        await using var stream = new RepeatingByteStream(1, 1);
        var cType = "xxx/aaa";
        var file = new MObject(cType, stream);
        var name = $"smiley{_nextFileId}.png";
        var result = await DoSubmitMediaCreationAsync(name, file, uid,
            dbContext, _cache, rLogger, token);
        var slug = result.RequireInsertSuccess(_logger);
            
        _logger.LogInformation("Rename media");
        var newSlug = $"smileyX{_nextFileId}.png";
        var command = new Rename(newSlug);
        var manageResult = await DoSubmitRenameForNameAsync(slug, uid, command, dbContext, _cache, rLogger, token);
        newSlug = manageResult.RequireInsertSuccess(_logger, "rename");

        _logger.LogInformation("Fetch media");
        var fetchResult = (FileStreamHttpResult)await DoGetMediaForNameAsync(newSlug, uid, dbContext, _cache, token);
        Assert.Equal(cType, fetchResult.ContentType);
    }
    
    [Fact]
    public async Task TestCreateMedia_ThenCreateAnotherOne_ThenRenameWithSameNameToInvokeDuplicateResolution()
    {
        await using var dbContext = _contextFactory();
        var token = CancellationToken.None;
        var rLogger = _loggerFactory.CreateLogger<Routing>();
        var (_, uid) = await _nextUserAsync(dbContext, token);

        _logger.LogInformation("Create media");
        await using var stream = new RepeatingByteStream(1, 1);
        var cType = "xxx/aaa";
        var file = new MObject(cType, stream);
        var name = $"smiley{_nextFileId}.png";
        var result = await DoSubmitMediaCreationAsync(name, file, uid,
            dbContext, _cache, rLogger, token);
        var slug = result.RequireInsertSuccess(_logger);
            
        _logger.LogInformation("Create media 2");
        await using var stream2 = new RepeatingByteStream(1, 1);
        var file2 = new MObject(cType, stream2);
        var name2 = $"smiley{_nextFileId}.png";
        var result2 = await DoSubmitMediaCreationAsync(name2, file2, uid,
            dbContext, _cache, rLogger, token);
        var slug2 = result2.RequireInsertSuccess(_logger);
            
        _logger.LogInformation("Rename media");
        var command = new Rename(slug2);
        var manageResult = await DoSubmitRenameForNameAsync(slug, uid, command, dbContext, _cache, rLogger, token);
        var newName = manageResult.RequireInsertSuccess(_logger, "rename");
        // one dot for the dup resolution and one dot for the extension
        Assert.Equal(2, newName.Where(c => c == '.').Length());
    }
    
    [Fact]
    public async Task TestRenameMedia_FailsForMissing()
    {
        await using var dbContext = _contextFactory();
        var token = CancellationToken.None;
        var rLogger = _loggerFactory.CreateLogger<Routing>();

        _logger.LogInformation("Rename entry");
        var newSlug = $"<Hello -{_nextFileId}>";
        var command = new Rename(newSlug);
        var manageResult = await DoSubmitRenameForNameAsync(IMPOSSIBLE_SLUG, Guid.Empty, command, 
            dbContext, _cache, rLogger, token);
        manageResult.Match(
            newName => Assert.Fail($"expected failCode=NotFound but got newName={newName}"),
            failCode => Assert.Equal(Failure.NotFound, failCode));
    }
    
    [Fact]
    public async Task TestCreateMedia_ThenRenameIt_FailsForPublic()
    {
        await using var dbContext = _contextFactory();
        var token = CancellationToken.None;
        var rLogger = _loggerFactory.CreateLogger<Routing>();
        var (_, uid) = await _nextUserAsync(dbContext, token);

        _logger.LogInformation("Create media");
        await using var stream = new RepeatingByteStream(1, 1);
        var cType = "xxx/aaa";
        var file = new MObject(cType, stream);
        var name = $"smiley{_nextFileId}.png";
        var result = await DoSubmitMediaCreationAsync(name, file, uid,
            dbContext, _cache, rLogger, token);
        var slug = result.RequireInsertSuccess(_logger);
            
        _logger.LogInformation("Attempt to rename media");
        var newSlug = $"smileyX{_nextFileId}.png";
        var command = new Rename(newSlug);
        var manageResult = await DoSubmitRenameForNameAsync(slug, Guid.Empty, command,
            dbContext, _cache, rLogger, token);
        manageResult.Match(
            newName => Assert.Fail($"expected failCode=NotPermitted but got newName={newName}"),
            failCode => Assert.Equal(Failure.NotPermitted, failCode));
    }
#endregion
#region Change post permissions tests
    [Fact]
    public async Task TestCreateMedia_ThenMakeItPublic()
    {
        await using var dbContext = _contextFactory();
        var token = CancellationToken.None;
        var rLogger = _loggerFactory.CreateLogger<Routing>();
        var (_, uid) = await _nextUserAsync(dbContext, token);

        _logger.LogInformation("Create media");
        await using var stream = new RepeatingByteStream(1, 1);
        var cType = "xxx/aaa";
        var file = new MObject(cType, stream);
        var name = $"smiley{_nextFileId}.png";
        var result = await DoSubmitMediaCreationAsync(name, file, uid,
            dbContext, _cache, rLogger, token);
        var slug = result.RequireInsertSuccess(_logger);
            
        _logger.LogInformation("Change permissions");
        var command = new SetPermissions(new Permissions
        {
            Public = true
        });
        var manageResult = await DoSubmitChangePermissionsForNameAsync(slug, uid, command, 
            dbContext, _cache, rLogger, token);
        manageResult.Match(
            failCode => Assert.Fail($"chperm failed: {failCode}"),
            () => _logger.LogInformation("chperm success"));
    }
    
    [Fact]
    public async Task TestCreateMedia_ThenMakeItPublic_ThenFetchItPublicly()
    {
        await using var dbContext = _contextFactory();
        var token = CancellationToken.None;
        var rLogger = _loggerFactory.CreateLogger<Routing>();
        var (_, uid) = await _nextUserAsync(dbContext, token);

        _logger.LogInformation("Create media");
        await using var stream = new RepeatingByteStream(1, 1);
        var cType = "xxx/aaa";
        var file = new MObject(cType, stream);
        var name = $"smiley{_nextFileId}.png";
        var result = await DoSubmitMediaCreationAsync(name, file, uid,
            dbContext, _cache, rLogger, token);
        var slug = result.RequireInsertSuccess(_logger);
            
        _logger.LogInformation("Change permissions");
        var command = new SetPermissions(new Permissions
        {
            Public = true
        });
        var manageResult = await DoSubmitChangePermissionsForNameAsync(slug, uid, command, 
            dbContext, _cache, rLogger, token);
        manageResult.Match(
            failCode => Assert.Fail($"chperm failed: {failCode}"),
            () => _logger.LogInformation("chperm success"));
        
        _logger.LogInformation("Fetch entry publicly");
        var entry = await DoGetMediaForNameAsync(slug, Guid.Empty, dbContext, _cache, token);
        Assert.IsType<FileStreamHttpResult>(entry);
    }
    
    // currently, revoking public only does cache invalidation but leave it in unit tests for branch coverage
    [Fact]
    public async Task TestCreateMedia_ThenMakeItPublic_ThenMakeItPrivateAgain()
    {
        await using var dbContext = _contextFactory();
        var token = CancellationToken.None;
        var rLogger = _loggerFactory.CreateLogger<Routing>();
        var (_, uid) = await _nextUserAsync(dbContext, token);

        _logger.LogInformation("Create media");
        await using var stream = new RepeatingByteStream(1, 1);
        var cType = "xxx/aaa";
        var file = new MObject(cType, stream);
        var name = $"smiley{_nextFileId}.png";
        var result = await DoSubmitMediaCreationAsync(name, file, uid,
            dbContext, _cache, rLogger, token);
        var slug = result.RequireInsertSuccess(_logger);
            
        _logger.LogInformation("Change permissions");
        var command = new SetPermissions(new Permissions
        {
            Public = true
        });
        var manageResult = await DoSubmitChangePermissionsForNameAsync(slug, uid, command, 
            dbContext, _cache, rLogger, token);
        manageResult.Match(
            failCode => Assert.Fail($"chperm failed: {failCode}"),
            () => _logger.LogInformation("chperm success"));
        
        _logger.LogInformation("Change permissions back");
        command = new SetPermissions(new Permissions());
        manageResult = await DoSubmitChangePermissionsForNameAsync(slug, uid, command, 
            dbContext, _cache, rLogger, token);
        manageResult.Match(
            failCode => Assert.Fail($"chperm failed: {failCode}"),
            () => _logger.LogInformation("chperm success"));
    }
    
    [Fact]
    public async Task TestChangeMediaPermissions_FailsForMissing()
    {
        await using var dbContext = _contextFactory();
        var token = CancellationToken.None;
        var rLogger = _loggerFactory.CreateLogger<Routing>();

        var command = new SetPermissions(new Permissions());
        var manageResult = await DoSubmitChangePermissionsForNameAsync(IMPOSSIBLE_SLUG, Guid.Empty, command, 
            dbContext, _cache, rLogger, token);
        manageResult.Match(
            failCode => Assert.Equal(Failure.NotFound, failCode),
            () => Assert.Fail($"expected error but got success"));
    }
    
    [Fact]
    public async Task TestCreatePost_ThenChangeIt_FailsForPublic()
    {
        await using var dbContext = _contextFactory();
        var token = CancellationToken.None;
        var rLogger = _loggerFactory.CreateLogger<Routing>();
        var (_, uid) = await _nextUserAsync(dbContext, token);

        _logger.LogInformation("Create media");
        await using var stream = new RepeatingByteStream(1, 1);
        var cType = "xxx/aaa";
        var file = new MObject(cType, stream);
        var name = $"smiley{_nextFileId}.png";
        var result = await DoSubmitMediaCreationAsync(name, file, uid,
            dbContext, _cache, rLogger, token);
        var slug = result.RequireInsertSuccess(_logger);
            
        _logger.LogInformation("Try to change permissions");
        var command = new SetPermissions(new Permissions());
        var manageResult = await DoSubmitChangePermissionsForNameAsync(slug, Guid.Empty, command, 
            dbContext, _cache, rLogger, token);
        manageResult.Match(
            failCode => Assert.Equal(Failure.NotPermitted, failCode),
            () => Assert.Fail("expected error but got success"));
    }
#endregion
#region Change post author tests
    [Fact]
    public async Task TestCreateMedia_ThenChangeAuthor()
    {
        await using var dbContext = _contextFactory();
        var token = CancellationToken.None;
        var rLogger = _loggerFactory.CreateLogger<Routing>();
        var (_, uid) = await _nextUserAsync(dbContext, token);
        var (email2, _) = await _nextUserAsync(dbContext, token);

        _logger.LogInformation("Create media");
        await using var stream = new RepeatingByteStream(1, 1);
        var cType = "xxx/aaa";
        var file = new MObject(cType, stream);
        var name = $"smiley{_nextFileId}.png";
        var result = await DoSubmitMediaCreationAsync(name, file, uid,
            dbContext, _cache, rLogger, token);
        var slug = result.RequireInsertSuccess(_logger);
            
        _logger.LogInformation("Change entry author");
        var command = new SetAuthor(email2);
        var manageResult = await DoSubmitSetAuthorForNameAsync(slug, uid, false, command, 
            dbContext, _cache, rLogger, token);
        manageResult.Match(
            newName => _logger.LogInformation("change author success: {newName}", newName),
            failCode => Assert.Fail($"change author failed: {failCode}"));
    }
    
    [Fact]
    public async Task TestCreateMedia_ThenChangeAuthor_ThenFetchIt_FailsForOldUid()
    {
        await using var dbContext = _contextFactory();
        var token = CancellationToken.None;
        var rLogger = _loggerFactory.CreateLogger<Routing>();
        var (_, uid) = await _nextUserAsync(dbContext, token);
        var (email2, uid2) = await _nextUserAsync(dbContext, token);
        
        _logger.LogInformation("Create media");
        await using var stream = new RepeatingByteStream(1, 1);
        var cType = "xxx/aaa";
        var file = new MObject(cType, stream);
        var name = $"smiley{_nextFileId}.png";
        var result = await DoSubmitMediaCreationAsync(name, file, uid,
            dbContext, _cache, rLogger, token);
        var slug = result.RequireInsertSuccess(_logger);
            
        _logger.LogInformation("Change entry author");
        var command = new SetAuthor(email2);
        var manageResult = await DoSubmitSetAuthorForNameAsync(slug, uid, false, command, 
            dbContext, _cache, rLogger, token);
        manageResult.Match(
            newName => _logger.LogInformation("change author success: {newName}", newName),
            failCode => Assert.Fail($"change author failed: {failCode}"));

        _logger.LogInformation("Attempt to fetch with old uid");
        var entry = await DoGetMediaForNameAsync(slug, uid, dbContext, _cache, token);
        Assert.IsType<ForbidHttpResult>(entry);
    }
    
    [Fact]
    public async Task TestCreateMedia_ThenChangeAuthor_ThenFetchIt()
    {
        await using var dbContext = _contextFactory();
        var token = CancellationToken.None;
        var rLogger = _loggerFactory.CreateLogger<Routing>();
        var (_, uid) = await _nextUserAsync(dbContext, token);
        var (email2, uid2) = await _nextUserAsync(dbContext, token);

        _logger.LogInformation("Create media");
        await using var stream = new RepeatingByteStream(1, 1);
        var cType = "xxx/aaa";
        var file = new MObject(cType, stream);
        var name = $"smiley{_nextFileId}.png";
        var result = await DoSubmitMediaCreationAsync(name, file, uid,
            dbContext, _cache, rLogger, token);
        var slug = result.RequireInsertSuccess(_logger);
            
        _logger.LogInformation("Change entry author");
        var command = new SetAuthor(email2);
        var manageResult = await DoSubmitSetAuthorForNameAsync(slug, uid, false, command, 
            dbContext, _cache, rLogger, token);
        manageResult.Match(
            newName => _logger.LogInformation("change author success: {newName}", newName),
            failCode => Assert.Fail($"change author failed: {failCode}"));
            
        _logger.LogInformation("Fetch entry publicly");
        var entry = await DoGetMediaForNameAsync(slug, uid2, dbContext, _cache, token);
        Assert.IsType<FileStreamHttpResult>(entry);
    }
    
    [Fact]
    public async Task TestChangeMediaAuthor_FailsForMissing()
    {
        await using var dbContext = _contextFactory();
        var token = CancellationToken.None;
        var rLogger = _loggerFactory.CreateLogger<Routing>();

        _logger.LogInformation("Attempt to set author");
        var command = new SetAuthor("-");
        var manageResult = await DoSubmitSetAuthorForNameAsync(IMPOSSIBLE_SLUG, Guid.Empty, false, command, 
            dbContext, _cache, rLogger, token);
        manageResult.Match(
            newName => Assert.Fail($"expected failCode=NotFound but got newName={newName}"),
            failCode => Assert.Equal(Failure.NotFound, failCode));
    }
    
    [Fact]
    public async Task TestCreateMedia_ThenChangeAuthor_FailsForPublic()
    {
        await using var dbContext = _contextFactory();
        var token = CancellationToken.None;
        var rLogger = _loggerFactory.CreateLogger<Routing>();
        var (_, uid) = await _nextUserAsync(dbContext, token);
        var (email2, _) = await _nextUserAsync(dbContext, token);

        _logger.LogInformation("Create media");
        await using var stream = new RepeatingByteStream(1, 1);
        var cType = "xxx/aaa";
        var file = new MObject(cType, stream);
        var name = $"smiley{_nextFileId}.png";
        var result = await DoSubmitMediaCreationAsync(name, file, uid,
            dbContext, _cache, rLogger, token);
        var slug = result.RequireInsertSuccess(_logger);
            
        _logger.LogInformation("Attempt to change entry author");
        var command = new SetAuthor(email2);
        var manageResult = await DoSubmitSetAuthorForNameAsync(slug, Guid.Empty, false, command, 
            dbContext, _cache, rLogger, token);
        manageResult.Match(
            newAuthor => Assert.Fail($"expected failCode=NotPermitted but got newAuthor={newAuthor}"),
            failCode => Assert.Equal(Failure.NotPermitted, failCode));
    }
    
    [Fact]
    public async Task TestCreatePost_ThenChangeAuthor_FailsForInvalidNewAuthor()
    {
        await using var dbContext = _contextFactory();
        var token = CancellationToken.None;
        var rLogger = _loggerFactory.CreateLogger<Routing>();
        var (_, uid) = await _nextUserAsync(dbContext, token);
        
        _logger.LogInformation("Create media");
        await using var stream = new RepeatingByteStream(1, 1);
        var cType = "xxx/aaa";
        var file = new MObject(cType, stream);
        var name = $"smiley{_nextFileId}.png";
        var result = await DoSubmitMediaCreationAsync(name, file, uid,
            dbContext, _cache, rLogger, token);
        var slug = result.RequireInsertSuccess(_logger);
            
        _logger.LogInformation("Attempt to change entry author");
        var command = new SetAuthor("-");
        var manageResult = await DoSubmitSetAuthorForNameAsync(slug, Guid.Empty, false, command, 
            dbContext, _cache, rLogger, token);
        manageResult.Match(
            newAuthor => Assert.Fail($"expected failCode=NotPermitted but got newAuthor={newAuthor}"),
            failCode => Assert.Equal(Failure.NotPermitted, failCode));
    }
#endregion
#region Delete post tests
    [Fact]
    public async Task TestCreateMedia_ThenDeleteIt()
    {
        await using var dbContext = _contextFactory();
        var token = CancellationToken.None;
        var rLogger = _loggerFactory.CreateLogger<Routing>();
        var (_, uid) = await _nextUserAsync(dbContext, token);

        _logger.LogInformation("Create media");
        await using var stream = new RepeatingByteStream(1, 1);
        var cType = "xxx/aaa";
        var file = new MObject(cType, stream);
        var name = $"smiley{_nextFileId}.png";
        var result = await DoSubmitMediaCreationAsync(name, file, uid,
            dbContext, _cache, rLogger, token);
        var slug = result.RequireInsertSuccess(_logger);
            
        _logger.LogInformation("Delete media");
        var manageResult = await DoDeleteMediumAsync(slug, false, uid, dbContext, _cache, rLogger, token);
        manageResult.IfSome(failCode => Assert.Fail($"delete failed: {failCode}"));
    }
    
    [Fact]
    public async Task TestCreateMedia_ThenDelete_ThenFetchItFails()
    {
        await using var dbContext = _contextFactory();
        var token = CancellationToken.None;
        var rLogger = _loggerFactory.CreateLogger<Routing>();
        var (_, uid) = await _nextUserAsync(dbContext, token);

        _logger.LogInformation("Create media");
        await using var stream = new RepeatingByteStream(1, 1);
        var cType = "xxx/aaa";
        var file = new MObject(cType, stream);
        var name = $"smiley{_nextFileId}.png";
        var result = await DoSubmitMediaCreationAsync(name, file, uid,
            dbContext, _cache, rLogger, token);
        var slug = result.RequireInsertSuccess(_logger);
            
        _logger.LogInformation("Delete media");
        var manageResult = await DoDeleteMediumAsync(slug, false, uid, dbContext, _cache, rLogger, token);
        manageResult.IfSome(failCode => Assert.Fail($"delete failed: {failCode}"));
        
        var fetchResult = await DoGetMediaForNameAsync(slug, uid, dbContext, _cache, token);
        Assert.IsType<NotFound>(fetchResult);
    }
    
    [Fact]
    public async Task TestCreateMedia_ThenDeleteIt_FailsPublicly()
    {
        await using var dbContext = _contextFactory();
        var token = CancellationToken.None;
        var rLogger = _loggerFactory.CreateLogger<Routing>();
        var (_, uid) = await _nextUserAsync(dbContext, token);

        _logger.LogInformation("Create media");
        await using var stream = new RepeatingByteStream(1, 1);
        var cType = "xxx/aaa";
        var file = new MObject(cType, stream);
        var name = $"smiley{_nextFileId}.png";
        var result = await DoSubmitMediaCreationAsync(name, file, uid,
            dbContext, _cache, rLogger, token);
        var slug = result.RequireInsertSuccess(_logger);
            
        _logger.LogInformation("Delete media");
        var manageResult = await DoDeleteMediumAsync(slug, false, Guid.Empty, dbContext, _cache, rLogger, token);
        manageResult.Match(failCode => Assert.Equal(Failure.NotPermitted, failCode),
            () => Assert.Fail("expected failCode=NotPermitted but got success"));
    }
    
    [Fact]
    public async Task TestDeleteMedia_FailsForMissing()
    {
        await using var dbContext = _contextFactory();
        var token = CancellationToken.None;
        var rLogger = _loggerFactory.CreateLogger<Routing>();

        _logger.LogInformation("Delete media");
        var manageResult = await DoDeleteMediumAsync(IMPOSSIBLE_SLUG, false, Guid.Empty,
            dbContext, _cache, rLogger, token);
        manageResult.Match(failCode => Assert.Equal(Failure.NotFound, failCode),
            () => Assert.Fail("expected failCode=NotFound but got success"));
    }
#endregion
}