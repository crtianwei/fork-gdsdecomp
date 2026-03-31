using ICSharpCode.Decompiler.CSharp.Syntax;
using ICSharpCode.Decompiler.CSharp.Transforms;
using ICSharpCode.Decompiler.Semantics;
using ICSharpCode.Decompiler.TypeSystem;

namespace GodotMonoDecomp.Transforms;

/// <summary>
/// Restores source-level shape for System.Text.Json source-generation context classes.
/// </summary>
public class RemoveJsonSourceGenerationClassBody : DepthFirstAstVisitor, IAstTransform
{
	private const string GeneratedCodeAttributeFullName = "System.CodeDom.Compiler.GeneratedCodeAttribute";
	private const string JsonSerializableAttributeFullName = "System.Text.Json.Serialization.JsonSerializableAttribute";
	private const string JsonSourceGeneratorName = "System.Text.Json.SourceGeneration";
	private const string JsonTypeInfoFullName = "System.Text.Json.Serialization.Metadata.JsonTypeInfo";

	public void Run(AstNode rootNode, TransformContext context)
	{
		rootNode.AcceptVisitor(this);
	}

	public override void VisitTypeDeclaration(TypeDeclaration typeDeclaration)
	{
		if (typeDeclaration.ClassType != ClassType.Class)
		{
			base.VisitTypeDeclaration(typeDeclaration);
			return;
		}

		bool hasJsonSourceGeneratorGeneratedCode = Common.RemoveGeneratedCodeAttributes(
			typeDeclaration.Attributes,
			JsonSourceGeneratorName
		);
		if (!hasJsonSourceGeneratorGeneratedCode)
		{
			base.VisitTypeDeclaration(typeDeclaration);
			return;
		}
		// get all the parameters for the `JsonSerializable` attribute
		List<IType> jsonTypes = typeDeclaration.Attributes
			.SelectMany(attribute => attribute.Attributes)
			.Where(attribute => Common.IsAttribute(attribute, JsonSerializableAttributeFullName, "JsonSerializable"))
			.SelectMany(attribute => attribute.Arguments)
			.Select(argument =>
			{
				if (argument is TypeOfExpression typeOfExpression)
				{
					return typeOfExpression.Type.Annotation<TypeResolveResult>()?.Type;
				}
				return null;
			})
			.OfType<IType>()
			.Distinct()
			.ToList();

		typeDeclaration.Modifiers |= Modifiers.Partial;
		var members = typeDeclaration.Members.ToArray();
		int firstJsonTypeInfoFieldIndex = GetFirstIndex(members, member =>
			member is FieldDeclaration field && IsJsonTypeInfoTypeWithTemplateArgumentsOf(field.ReturnType, jsonTypes)
		);
		int firstJsonTypeInfoPropertyIndex = GetFirstIndex(members, member =>
			member is PropertyDeclaration property && IsJsonTypeInfoTypeWithTemplateArgumentsOf(property.ReturnType, jsonTypes)
		);
		int firstJsonTypeInfoMethodIndex = GetFirstIndex(members, member =>
			member is MethodDeclaration method && IsJsonTypeInfoTypeWithTemplateArgumentsOf(method.ReturnType, jsonTypes)
		);

		for (int i = 0; i < members.Length; i++)
		{
			var member = members[i];
			if (ShouldPreserveMember(
					member,
					i,
					firstJsonTypeInfoFieldIndex,
					firstJsonTypeInfoPropertyIndex,
					firstJsonTypeInfoMethodIndex))
			{
				continue;
			}

			member.Remove();
		}
	}

	private static bool ShouldPreserveMember(
		EntityDeclaration member,
		int memberIndex,
		int firstJsonTypeInfoFieldIndex,
		int firstJsonTypeInfoPropertyIndex,
		int firstJsonTypeInfoMethodIndex)
	{
		// Preserve any leading user member before the generated JsonTypeInfo field section.
		if (firstJsonTypeInfoFieldIndex >= 0 && memberIndex < firstJsonTypeInfoFieldIndex)
		{
			return true;
		}

		// Preserve leading user properties before generated JsonTypeInfo properties.
		if (member is PropertyDeclaration
			&& firstJsonTypeInfoPropertyIndex >= 0
			&& memberIndex < firstJsonTypeInfoPropertyIndex)
		{
			return true;
		}

		// Preserve leading user methods before generated JsonTypeInfo-returning methods.
		if (member is MethodDeclaration
			&& firstJsonTypeInfoMethodIndex >= 0
			&& memberIndex < firstJsonTypeInfoMethodIndex)
		{
			return true;
		}

		return false;
	}

	private static int GetFirstIndex(EntityDeclaration[] members, Func<EntityDeclaration, bool> predicate)
	{
		for (int i = 0; i < members.Length; i++)
		{
			if (predicate(members[i]))
			{
				return i;
			}
		}

		return -1;
	}

	private static bool IsJsonTypeInfoType(AstType type)
	{
		if (type.IsNull)
		{
			return false;
		}

		if (type.Annotation<TypeResolveResult>() is { Type: { } resolvedType })
		{
			if (string.Equals(resolvedType.FullName, JsonTypeInfoFullName, StringComparison.Ordinal))
			{
				return true;
			}

			if (resolvedType.GetDefinition() is ITypeDefinition definition
				&& string.Equals(definition.FullName, JsonTypeInfoFullName, StringComparison.Ordinal))
			{
				return true;
			}
		}

		// Fallback for cases where type resolution annotations are unavailable.
		return type.ToString().Contains("JsonTypeInfo", StringComparison.Ordinal);
	}

	private static bool IsJsonTypeInfoTypeWithTemplateArgumentsOf(AstType type, IEnumerable<IType> jsonTypes)
	{
		if (IsJsonTypeInfoType(type))
		{
			if (type.Annotation<TypeResolveResult>() is { Type: { } resolvedType })
			{
				if (resolvedType.TypeArguments.All(parameter => jsonTypes.Any(jsonType =>
				{
					return jsonType.Equals(parameter.GetDefinition());
				})))
				{
					return true;
				}
			}
		}
		return false;
	}


}
