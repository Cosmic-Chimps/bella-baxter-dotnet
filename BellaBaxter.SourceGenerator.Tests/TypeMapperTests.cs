using BellaBaxter.SourceGenerator;
using Xunit;

namespace BellaBaxter.SourceGenerator.Tests;

public class TypeMapperTests
{
    [Theory]
    [InlineData("String",           "string")]
    [InlineData("Integer",          "int")]
    [InlineData("Float",            "double")]
    [InlineData("Boolean",          "bool")]
    [InlineData("Guid",             "System.Guid")]
    [InlineData("Base64",           "byte[]")]
    [InlineData("Url",              "System.Uri")]
    [InlineData("Json",             "string")]
    [InlineData("ConnectionString", "string")]
    [InlineData("CertificatePem",   "string")]
    [InlineData("unknown",          "string")]   // fallback
    public void GetCSharpType_ReturnsMappedType(string bellaType, string expectedCsType)
    {
        Assert.Equal(expectedCsType, TypeMapper.GetCSharpType(bellaType));
    }

    [Fact]
    public void BuildGetterExpression_Integer_WrapsInIntParse()
    {
        var expr = TypeMapper.BuildGetterExpression("Integer", "_env(\"PORT\")");
        Assert.Equal("int.Parse(_env(\"PORT\"))", expr);
    }

    [Fact]
    public void BuildGetterExpression_Boolean_WrapsInStringEquals()
    {
        var expr = TypeMapper.BuildGetterExpression("Boolean", "_env(\"FLAG\")");
        Assert.Contains("string.Equals", expr);
        Assert.Contains("\"true\"", expr);
    }

    [Fact]
    public void BuildGetterExpression_String_ReturnsPlainExpr()
    {
        var expr = TypeMapper.BuildGetterExpression("String", "_env(\"KEY\")");
        Assert.Equal("_env(\"KEY\")", expr);
    }

    [Fact]
    public void BuildGetterExpression_Url_ReturnsNewUri()
    {
        var expr = TypeMapper.BuildGetterExpression("Url", "_env(\"SERVICE_URL\")");
        Assert.Equal("new System.Uri(_env(\"SERVICE_URL\"))", expr);
    }
}
