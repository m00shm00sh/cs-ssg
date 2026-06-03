using LanguageExt;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;

using CsSsg.Src.Exceptions;
using CsSsg.Src.Post;

namespace CsSsg.Test.Post;

public class ModelsTest
{
    #region Contents

    [Fact]
    public void VerifyContents_SlugGeneration_WorksStatically()
    {
        const string s = "aa#bb$cc";
        const string expSlug = "aa-bb-cc";
        Assert.Equal(expSlug, Contents.ComputeSlugName(s));
    }

    [Fact]
    public void VerifyContents_SlugGeneration_OmitsInvalidCharacters()
    {
        var c = new Contents("aa!bb@cc", "");
        const string expSlug = "aa-bb-cc";
        Assert.Equal(expSlug, c.ComputeSlugName());
    }

    [Fact]
    public void VerifyContents_SlugGeneration_MergesSuccessiveReplacementCharacters()
    {
        const string s = "aa  bb- cc";
        const string expSlug = "aa-bb-cc";
        Assert.Equal(expSlug, Contents.ComputeSlugName(s));
    }

    [Fact]
    public void VerifyContents_SlugGeneration_Trims()
    {
        const string s = "!aa!bb!";
        const string expSlug = "aa-bb";
        Assert.Equal(expSlug, Contents.ComputeSlugName(s));
    }

    [Fact]
    public void VerifyContents_SlugGeneration_HandlesUnicode()
    {
        const string s = "-你好-";
        const string expSlug = "你好";
        Assert.Equal(expSlug, Contents.ComputeSlugName(s));
    }

    #endregion

    #region EditorFormContents

    [Fact]
    public void VerifyEditorFormContents_RebindsToContents()
    {
        var titleText = "ab";
        var bodyText = "# bc";
        var efc = new EditorFormContents(titleText, bodyText);
        var exp = new Contents(titleText, bodyText);
        Assert.Equal(exp, (Contents)efc);
    }

    #endregion

    #region ManageCommand - Form parsing

    [Fact]
    public void VerifyManageCommand_FormParsing_Rename()
    {
        const string renameTo = "renameTo";
        var formData = new FormCollection(new Dictionary<string, StringValues>
        {
            ["newname"] = renameTo
        });
        var cmd = IManageCommand.FromForm(formData, IManageCommand.FormFrom.Rename)
            .RequireSuccess<IManageCommand.Rename>();
        Assert.NotNull(cmd);
        Assert.Equal(renameTo, cmd.RenameTo);
    }

    [Theory]
    [InlineData(" ")]
    [InlineData(null)]
    public void VerifyManageCommand_FormParsing_InvalidRename(string? newAuthor)
    {
        var formData = new FormCollection(new Dictionary<string, StringValues>
        {
            ["newname"] = newAuthor,
        });
        var exMsg = IManageCommand.FromForm(formData, IManageCommand.FormFrom.Rename)
            .RequireFailure();
        Assert.Contains("missing or invalid parameter", exMsg);
    }

    public static IList<object?[]> TestDataForParseSetTagsForm()
    {
        List<object?[]> l =
        [
            [
                "public", null,
                new IManageCommand.PostTags(IManageCommand.PostVisibility.Public, [])
            ],
            [
                "unlisted", null,
                new IManageCommand.PostTags(IManageCommand.PostVisibility.Unlisted, [])
            ],
            [
                "public,unlisted", null,
                new IManageCommand.PostTags(IManageCommand.PostVisibility.Tags, [])
            ],
            [
                "public", "public",
                new IManageCommand.PostTags(IManageCommand.PostVisibility.Public, [])
            ],
            [
                "public", "unlisted",
                new IManageCommand.PostTags(IManageCommand.PostVisibility.Public, [])
            ],
            [
                "public", "A&B",
                new IManageCommand.PostTags(IManageCommand.PostVisibility.Public, ["a-b"])
            ],
            [
                null, "A&B -C",
                new IManageCommand.PostTags(IManageCommand.PostVisibility.Tags, ["a-b", "c"])
            ],
        ];
        return l;
    }

    [Theory]
    [MemberData(nameof(TestDataForParseSetTagsForm))]
    public void VerifyManageCommand_FormParsing_SetTags(string? visibilityValue, string? tagsValue,
        IManageCommand.PostTags result)
    {
        Assert.All(result.Tags, s => Assert.True(Contents.ComputeSlugName(s) == s, "the expected tag is invalid"));
        var formData = new FormCollection(new Dictionary<string, StringValues>
        {
            ["visibility"] = visibilityValue,
            ["tags"] = tagsValue
        });
        var cmd = IManageCommand.FromForm(formData, IManageCommand.FormFrom.Tags)
            .RequireSuccess<IManageCommand.SetTags>();
        Assert.NotNull(cmd);
        Assert.Equal(result, cmd.Tags, PostTagsEqualityComparer.Instance);
    }

    [Fact]
    public void VerifyManageCommand_FormParsing_NewAuthor()
    {
        const string newAuthor = "fred@";
        var formData = new FormCollection(new Dictionary<string, StringValues>
        {
            ["newauthor"] = newAuthor
        });
        var cmd = IManageCommand.FromForm(formData, IManageCommand.FormFrom.Author)
            .RequireSuccess<IManageCommand.SetAuthor>();
        Assert.NotNull(cmd);
        Assert.Equal(newAuthor, cmd.NewAuthor);
    }

    [Theory]
    [InlineData(" ")]
    [InlineData(null)]
    public void VerifyManageCommand_FormParsing_InvalidNewAuthor(string? newAuthor)
    {
        var formData = new FormCollection(new Dictionary<string, StringValues>
        {
            ["newauthor"] = newAuthor
        });
        var exMsg = IManageCommand.FromForm(formData, IManageCommand.FormFrom.Author)
            .RequireFailure();
        Assert.Contains("missing or invalid parameter", exMsg);
    }

    [Fact]
    public void VerifyManageCommand_FormParsing_ConfirmDelete()
    {
        var formData = new FormCollection(new Dictionary<string, StringValues>
        {
            ["cb_delete"] = "ON"
        });
        IManageCommand.FromForm(formData, IManageCommand.FormFrom.Delete)
            .RequireSuccess<IManageCommand.Delete>();
    }

    [Fact]
    public void VerifyManageCommand_FormParsing_InvalidDelete()
    {
        var formData = new FormCollection(new Dictionary<string, StringValues>());
        var exMsg = IManageCommand.FromForm(formData, IManageCommand.FormFrom.Delete)
            .RequireFailure();
        Assert.Contains("missing or invalid parameter", exMsg);
    }

    [Fact]
    public void VerifyManageCommand_FormParsing_InvalidNone()
    {
        var formData = new FormCollection(new Dictionary<string, StringValues>());
        Assert.Throws<UnexpectedEnumValueException>(() =>
            IManageCommand.FromForm(formData, default)
        );
    }

    #endregion
}

file static class ResultExtensions
{
    extension(Either<ArgumentException, IManageCommand> parseResult)
    {
        internal T RequireSuccess<T>()
            where T : IManageCommand
        {
            IManageCommand result = null!;
            parseResult.Match(succ => result = succ,
                fail => Assert.Fail(fail.Message));
            return (T)result;
        }

        internal string RequireFailure()
        {
            string exMsg = null!;
            parseResult.Match(succ => Assert.Fail("expected failure but got success"),
                ex => exMsg = ex.Message);
            return exMsg;
        }
    }
}