using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using Xunit.Sdk;

using CsSsg.Src.Post;

namespace CsSsg.Test.Post;

public class ModelsTest
{
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
    public void VerifyManageCommand_FormParsing_Rename()
    {
        const string renameTo = "renameTo";
        var formData = new FormCollection(new Dictionary<string, StringValues>
        {
            ["a_rename"] = "Rename button",
            ["newname"] = renameTo
        });
        ManageCommand? mc = null;
        ManageCommand.FromForm(formData).Match(
            ex => throw ex,
            data => { mc = data; });
        Assert.Equal(renameTo, mc?.RenameTo);
        mc?.GetActiveCommand().Match(
            ex => throw ex,
            ac => Assert.Equal(ManageCommand.ActiveCommand.RenameTo, ac)
        );
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
            data => { throw new XunitException("failed to throw"); }
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
        ManageCommand? mc = null;
        ManageCommand.FromForm(formData).Match(
            ex => throw ex,
            data => { mc = data; });
        var perms = mc?.NewPermissions;
        Assert.Equal(newPublic, perms?.Public);
        mc?.GetActiveCommand().Match(
            ex => throw ex,
            ac => Assert.Equal(ManageCommand.ActiveCommand.NewPermissions, ac)
        );
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
        ManageCommand? mc = null;
        ManageCommand.FromForm(formData).Match(
            ex => throw ex,
            data => { mc = data; });
        Assert.Equal(newAuthor, mc?.ReassignAuthorTo);
        mc?.GetActiveCommand().Match(
            ex => throw ex,
            ac => Assert.Equal(ManageCommand.ActiveCommand.NewAuthor, ac)
        );
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
            data => throw new XunitException("failed to throw"));
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
        ManageCommand? mc = null;
        ManageCommand.FromForm(formData).Match(
            ex => throw ex,
            data => { mc = data; });
        Assert.Equal(true, mc?.ConfirmDelete);
        mc?.GetActiveCommand().Match(
            ex => throw ex,
            ac => Assert.Equal(ManageCommand.ActiveCommand.Delete, ac)
        );
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
            _ => throw new XunitException("failed to error"));
        Assert.Contains("missing or invalid parameter", exMsg);
    }

    [Fact]
    public void VerifyManageCommand_ActiveCommand_InvalidNone()
    {
        var mc = new ManageCommand();
        mc.GetActiveCommand().Match(
            ex => Assert.Contains("expected command", ex.Message),
            _ => throw new XunitException("failed to error"));
    }
    
    [Fact]
    public void VerifyManageCommand_ActiveCommand_InvalidAll()
    {
        var mc = new ManageCommand
        {
            RenameTo = "a",
            NewPermissions = new ManageCommand.Permissions(),
            ReassignAuthorTo = "b",
            ConfirmDelete = true
        };
        mc.GetActiveCommand().Match(
            ex => Assert.Contains("expected one command", ex.Message),
            _ => throw new XunitException("failed to error"));
    }
    
}