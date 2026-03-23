using CsSsg.Src.Exceptions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using Xunit.Sdk;

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
        IManageCommand.FromForm(formData, IManageCommand.FormFrom.Rename).Match(data =>
        {
            var cmd = data as IManageCommand.Rename;
            Assert.NotNull(cmd);
            Assert.Equal(renameTo, cmd.RenameTo);
        }, ex => Assert.Fail(ex.Message));
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
        string? exMsg = null;
        IManageCommand.FromForm(formData, IManageCommand.FormFrom.Rename).Match(
            _ => { Assert.Fail("failed to throw"); },
            ex => { exMsg = ex.Message; });
        Assert.Contains("missing or invalid parameter", exMsg);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void VerifyManageCommand_FormParsing_SetPublic(bool newPublic)
    {
        var formData = new FormCollection(new Dictionary<string, StringValues>
        {
            ["cb_public"] = newPublic ? "ON" : null
        });
        IManageCommand.FromForm(formData, IManageCommand.FormFrom.Permissions).Match(data =>
        {
            var cmd = data as IManageCommand.SetPermissions;
            Assert.NotNull(cmd);
            Assert.Equal(newPublic, cmd.Permissions.Public);
        }, ex => Assert.Fail(ex.Message));
    }
    
    [Fact]
    public void VerifyManageCommand_FormParsing_NewAuthor()
    {
        const string newAuthor = "fred@";
        var formData = new FormCollection(new Dictionary<string, StringValues>
        {
            ["newauthor"] = newAuthor
        });
        IManageCommand.FromForm(formData, IManageCommand.FormFrom.Author).Match(data =>
        {
            var cmd = data as IManageCommand.SetAuthor;
            Assert.NotNull(cmd);
            Assert.Equal(newAuthor, cmd.NewAuthor);
        }, ex => Assert.Fail(ex.Message));
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
        string? exMsg = null;
        IManageCommand.FromForm(formData, IManageCommand.FormFrom.Author).Match(
            _ => Assert.Fail("failed to throw"),
            ex => { exMsg = ex.Message; });
        Assert.Contains("missing or invalid parameter", exMsg);
    }
    
    [Fact]
    public void VerifyManageCommand_FormParsing_ConfirmDelete()
    {
        var formData = new FormCollection(new Dictionary<string, StringValues>
        {
            ["cb_delete"] = "ON"
        });
        IManageCommand.FromForm(formData, IManageCommand.FormFrom.Delete).Match(data =>
        {
            var cmd = data as IManageCommand.Delete;
            Assert.NotNull(cmd);
        },
        ex => Assert.Fail(ex.Message));
    }
    
    [Fact]
    public void VerifyManageCommand_FormParsing_InvalidDelete()
    {
        var formData = new FormCollection(new Dictionary<string, StringValues>());
        string? exMsg = null;
        IManageCommand.FromForm(formData, IManageCommand.FormFrom.Delete).Match(
            _ => Assert.Fail("failed to error"),
            ex => { exMsg = ex.Message; });
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