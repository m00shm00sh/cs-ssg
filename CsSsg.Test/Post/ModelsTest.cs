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
#endregion
#region ManageCommand - Form parsing
    [Fact]
    public void VerifyManageCommand_FormParsing_Rename()
    {
        const string renameTo = "renameTo";
        var formData = new FormCollection(new Dictionary<string, StringValues>
        {
            ["a_rename"] = "Rename button",
            ["newname"] = renameTo
        });
        ManageCommand.FromForm(formData).Match(
            ex => Assert.Fail(ex.Message),
            data =>
            {
                var cmd = data as ManageCommand.Rename;
                Assert.NotNull(cmd);
                Assert.Equal(renameTo, cmd.RenameTo);
            });
    }
    
    [Theory]
    [InlineData(" ")]
    [InlineData(null)]
    public void VerifyManageCommand_FormParsing_InvalidRename(string? newAuthor)
    {
        var formData = new FormCollection(new Dictionary<string, StringValues>
        {
            ["a_rename"] = "Rename button",
            ["newname"] = newAuthor,
        });
        string? exMsg = null;
        ManageCommand.FromForm(formData).Match(
            ex => { exMsg = ex.Message; },
            _ => { Assert.Fail("failed to throw"); }
        );
        Assert.Contains("missing or invalid parameter", exMsg);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void VerifyManageCommand_FormParsing_SetPublic(bool newPublic)
    {
        var formData = new FormCollection(new Dictionary<string, StringValues>
        {
            ["a_perms"] = "Permissions button",
            ["cb_public"] = newPublic ? "ON" : null
        });
        ManageCommand.FromForm(formData).Match(
            ex => Assert.Fail(ex.Message),
            data =>
            {
                var cmd = data as ManageCommand.SetPermissions;
                Assert.NotNull(cmd);
                Assert.Equal(newPublic, cmd.Permissions.Public);
            });
    }
    
    [Fact]
    public void VerifyManageCommand_FormParsing_NewAuthor()
    {
        const string newAuthor = "fred@";
        var formData = new FormCollection(new Dictionary<string, StringValues>
        {
            ["a_author"] = "Author button",
            ["newauthor"] = newAuthor
        });
        ManageCommand.FromForm(formData).Match(
            ex => Assert.Fail(ex.Message),
            data =>
            {
                var cmd = data as ManageCommand.SetAuthor;
                Assert.NotNull(cmd);
                Assert.Equal(newAuthor, cmd.NewAuthor);
            });
    }
    
    [Theory]
    [InlineData(" ")]
    [InlineData(null)]
    public void VerifyManageCommand_FormParsing_InvalidNewAuthor(string? newAuthor)
    {
        var formData = new FormCollection(new Dictionary<string, StringValues>
        {
            ["a_author"] = "Author button",
            ["newauthor"] = newAuthor
        });
        string? exMsg = null;
        ManageCommand.FromForm(formData).Match(
            ex => { exMsg = ex.Message; },
            _ => Assert.Fail("failed to throw"));
        Assert.Contains("missing or invalid parameter", exMsg);
    }
    
    [Fact]
    public void VerifyManageCommand_FormParsing_ConfirmDelete()
    {
        var formData = new FormCollection(new Dictionary<string, StringValues>
        {
            ["a_delete"] = "Delete button",
            ["cb_delete"] = "ON"
        });
        ManageCommand.FromForm(formData).Match(
            ex => Assert.Fail(ex.Message),
            data =>
            {
                var cmd = data as ManageCommand.Delete;
                Assert.NotNull(cmd);
            });
    }
    
    [Fact]
    public void VerifyManageCommand_FormParsing_InvalidDelete()
    {
        var formData = new FormCollection(new Dictionary<string, StringValues>
        {
            ["a_delete"] = "Delete button",
        });
        string? exMsg = null;
        ManageCommand.FromForm(formData).Match(
            ex => { exMsg = ex.Message; },
            _ => Assert.Fail("failed to error"));
        Assert.Contains("missing or invalid parameter", exMsg);
    }

    [Fact]
    public void VerifyManageCommand_FormParsing_InvalidNone()
    {
        var formData = new FormCollection(new Dictionary<string, StringValues>());
        string? exMsg = null;
        ManageCommand.FromForm(formData).Match(
            ex => { exMsg = ex.Message; },
            _ => Assert.Fail("failed to error"));
        Assert.Contains("expected command", exMsg);
    }
    
    [Fact]
    public void VerifyManageCommand_FormParsing_InvalidAll()
    {
        var formData = new FormCollection(new Dictionary<string, StringValues>
        {
            ["a_rename"] = "Rename button",
            ["newname"] = "a",
            ["a_perms"] = "Permissions button",
            ["a_author"] = "Author button",
            ["newauthor"] = "b",
            ["a_delete"] = "Delete button",
            ["cb_delete"] = "ON"
        });
        string? exMsg = null;
        ManageCommand.FromForm(formData).Match(
            ex => { exMsg = ex.Message; },
            _ => Assert.Fail("failed to error"));
        Assert.Contains("expected one command", exMsg);
    }
#endregion
}