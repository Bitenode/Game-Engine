using System.Collections.Generic;
using Avalonia.Media;
using Microsoft.CodeAnalysis.Classification;

namespace Game_Engine.Core.Editor;

/// <summary>
/// Maps Roslyn classification kinds to brushes for the dark editor theme.
/// </summary>
public static class SyntaxTheme
{
    private static readonly IBrush s_default     = new SolidColorBrush(Color.Parse("#D4D4D4"));
    private static readonly IBrush s_keyword     = new SolidColorBrush(Color.Parse("#569CD6"));
    private static readonly IBrush s_controlKw   = new SolidColorBrush(Color.Parse("#C586C0"));
    private static readonly IBrush s_typeName    = new SolidColorBrush(Color.Parse("#4EC9B0"));
    private static readonly IBrush s_interface   = new SolidColorBrush(Color.Parse("#B8D7A3"));
    private static readonly IBrush s_stringLit   = new SolidColorBrush(Color.Parse("#CE9178"));
    private static readonly IBrush s_number      = new SolidColorBrush(Color.Parse("#B5CEA8"));
    private static readonly IBrush s_comment     = new SolidColorBrush(Color.Parse("#6A9955"));
    private static readonly IBrush s_xmlDoc      = new SolidColorBrush(Color.Parse("#608B4E"));
    private static readonly IBrush s_method      = new SolidColorBrush(Color.Parse("#DCDCAA"));
    private static readonly IBrush s_parameter   = new SolidColorBrush(Color.Parse("#9CDCFE"));
    private static readonly IBrush s_localVar    = new SolidColorBrush(Color.Parse("#9CDCFE"));
    private static readonly IBrush s_field       = new SolidColorBrush(Color.Parse("#9CDCFE"));
    private static readonly IBrush s_enumMember  = new SolidColorBrush(Color.Parse("#4FC1FF"));
    private static readonly IBrush s_preprocessor= new SolidColorBrush(Color.Parse("#9B9B9B"));
    private static readonly IBrush s_operator    = new SolidColorBrush(Color.Parse("#D4D4D4"));
    private static readonly IBrush s_punctuation = new SolidColorBrush(Color.Parse("#D4D4D4"));
    private static readonly IBrush s_namespace   = new SolidColorBrush(Color.Parse("#4EC9B0"));
    private static readonly IBrush s_property    = new SolidColorBrush(Color.Parse("#9CDCFE"));
    private static readonly IBrush s_constant    = new SolidColorBrush(Color.Parse("#4FC1FF"));

    private static readonly Dictionary<string, IBrush> s_map = new()
    {
        // Keywords
        [ClassificationTypeNames.Keyword]                   = s_keyword,
        [ClassificationTypeNames.ControlKeyword]            = s_controlKw,

        // Types
        [ClassificationTypeNames.ClassName]                 = s_typeName,
        [ClassificationTypeNames.StructName]                = s_typeName,
        [ClassificationTypeNames.RecordClassName]           = s_typeName,
        [ClassificationTypeNames.RecordStructName]          = s_typeName,
        [ClassificationTypeNames.EnumName]                  = s_typeName,
        [ClassificationTypeNames.DelegateName]              = s_typeName,
        [ClassificationTypeNames.TypeParameterName]         = s_typeName,
        [ClassificationTypeNames.InterfaceName]             = s_interface,
        [ClassificationTypeNames.NamespaceName]             = s_namespace,

        // Members
        [ClassificationTypeNames.MethodName]                = s_method,
        [ClassificationTypeNames.ExtensionMethodName]       = s_method,
        [ClassificationTypeNames.PropertyName]              = s_property,
        [ClassificationTypeNames.FieldName]                 = s_field,
        [ClassificationTypeNames.ConstantName]              = s_constant,
        [ClassificationTypeNames.EnumMemberName]            = s_enumMember,
        [ClassificationTypeNames.EventName]                 = s_property,
        [ClassificationTypeNames.ParameterName]             = s_parameter,
        [ClassificationTypeNames.LocalName]                 = s_localVar,
        [ClassificationTypeNames.LabelName]                 = s_localVar,

        // Literals
        [ClassificationTypeNames.NumericLiteral]            = s_number,
        [ClassificationTypeNames.StringLiteral]             = s_stringLit,
        [ClassificationTypeNames.VerbatimStringLiteral]     = s_stringLit,
        [ClassificationTypeNames.StringEscapeCharacter]     = s_stringLit,

        // Comments
        [ClassificationTypeNames.Comment]                   = s_comment,
        [ClassificationTypeNames.XmlDocCommentText]         = s_xmlDoc,
        [ClassificationTypeNames.XmlDocCommentComment]      = s_xmlDoc,
        [ClassificationTypeNames.XmlDocCommentDelimiter]    = s_xmlDoc,
        [ClassificationTypeNames.XmlDocCommentName]         = s_xmlDoc,
        [ClassificationTypeNames.XmlDocCommentAttributeName]= s_xmlDoc,
        [ClassificationTypeNames.XmlDocCommentAttributeQuotes]= s_xmlDoc,
        [ClassificationTypeNames.XmlDocCommentAttributeValue]= s_xmlDoc,
        [ClassificationTypeNames.XmlDocCommentEntityReference]= s_xmlDoc,
        [ClassificationTypeNames.XmlDocCommentProcessingInstruction]= s_xmlDoc,

        // Preprocessor
        [ClassificationTypeNames.PreprocessorKeyword]       = s_preprocessor,
        [ClassificationTypeNames.PreprocessorText]          = s_preprocessor,
        [ClassificationTypeNames.ExcludedCode]              = s_preprocessor,

        // Operators / Punctuation
        [ClassificationTypeNames.Operator]                  = s_operator,
        [ClassificationTypeNames.Punctuation]               = s_punctuation,
    };

    public static IBrush DefaultBrush => s_default;

    public static IBrush GetBrush(string classification)
        => s_map.TryGetValue(classification, out var b) ? b : s_default;
}
