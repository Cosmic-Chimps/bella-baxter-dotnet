// Maps Bella SecretType strings to C# return types and conversion expressions.

using System.Collections.Generic;

namespace BellaBaxter.SourceGenerator
{
    internal static class TypeMapper
    {
        private static readonly Dictionary<string, (string CSharpType, string EnvConversion)> Map =
            new Dictionary<string, (string, string)>
            {
                // type name (lower)        → C# return type,  expression using _env("KEY")
                { "string",           ("string",  "{0}")                                              },
                { "integer",          ("int",     "int.Parse({0})")                                   },
                { "float",            ("double",  "double.Parse({0}, System.Globalization.CultureInfo.InvariantCulture)") },
                { "boolean",          ("bool",    "string.Equals({0}, \"true\", System.StringComparison.OrdinalIgnoreCase)") },
                { "json",             ("string",  "{0}")                                              },
                { "base64",           ("byte[]",  "System.Convert.FromBase64String({0})")             },
                { "guid",             ("System.Guid", "System.Guid.Parse({0})")                      },
                { "connectionstring", ("string",  "{0}")                                              },
                { "url",              ("System.Uri", "new System.Uri({0})")                           },
                { "certificatepem",   ("string",  "{0}")                                              },
            };

        public static (string CSharpType, string EnvConversion) Resolve(string bellaType)
        {
            if (bellaType != null && Map.TryGetValue(bellaType.ToLowerInvariant(), out var result))
                return result;
            return ("string", "{0}");
        }

        /// <summary>
        /// Builds the property getter body.
        /// envExpr is typically: _env("KEY") for required, or _envOpt("KEY") for optional.
        /// </summary>
        public static string BuildGetterExpression(string bellaType, string envExpr)
        {
            var (_, conversion) = Resolve(bellaType);
            return string.Format(conversion, envExpr);
        }

        /// <summary>Returns the C# type name to use in the property declaration.</summary>
        public static string GetCSharpType(string bellaType)
        {
            return Resolve(bellaType).CSharpType;
        }
    }
}
