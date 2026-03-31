using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.CSharp.Syntax;
using ICSharpCode.Decompiler.CSharp.Transforms;
using ICSharpCode.Decompiler.TypeSystem;

namespace GodotMonoDecomp.Transforms;

/// <summary>
/// Intended to fix switch expressions that do not have enough context to determine the best type as a result of a parent member reference expression.
/// </summary>
/// <example>
/// e.g:
/// ```c#
///     public Animal GetAnimal(AnimalType animalType)
///     {
///         return (animalType switch
///         {
/// 			// at least one of these should have a cast to 'Animal' or the member reference expression below will cause the switch expression to fail to compile.
///             AnimalType.Dog => new Dog(),
///             AnimalType.Fish => new Fish(),
///             _ => throw new ArgumentException("Invalid animal type")
///         }).ToValidated();
///     }
/// ```
/// </example>
public class FixSwitchExpressionCasts : DepthFirstAstVisitor, IAstTransform
{
	private TransformContext? context;

	public void Run(AstNode rootNode, TransformContext context)
	{
		this.context = context;
		rootNode.AcceptVisitor(this);
	}

	private static bool IsCastableSwitchSectionExpressionBody(Expression body)
	{
		return body is not PrimitiveExpression && body is not NullReferenceExpression && body is not ThrowExpression;
	}

	public override void VisitSwitchExpression(SwitchExpression switchExpr)
	{
		if (context?.TypeSystemAstBuilder is null || switchExpr.Parent is not MemberReferenceExpression)
		{
			base.VisitSwitchExpression(switchExpr);
			return;
		}
		var resolved = switchExpr.GetResolveResult();

		if (!resolved.IsError && switchExpr.SwitchSections.Count > 1 && resolved.Type is not null)
		{
			if (!switchExpr.SwitchSections.Any(s => s.Body is CastExpression))
			{
				var resolvedTypeDefinition = resolved.Type.GetDefinition();
				HashSet<ITypeDefinition> allDefs = [];
				var mismatchedSections = switchExpr.SwitchSections.Where(s => {
					if (!IsCastableSwitchSectionExpressionBody(s.Body))
					{
						return false;
					}
					var rr = s.Body.GetResolveResult();
					if (rr is not null && !rr.IsError)
					{
						var def = rr.Type.GetDefinition();
						if (def is not null)
						{
							allDefs.Add(def);
							if (!def.Equals(resolvedTypeDefinition))
							{
								return true;
							}
						}

					}
					return false;
				}).ToArray();
				if (mismatchedSections.Length > 0 && allDefs.Count > 1) {
				    var first = mismatchedSections.FirstOrDefault()!;
					var body = first.Body;
					first.Body = null;
					first.Body = new CastExpression(context.TypeSystemAstBuilder.ConvertType(resolved.Type), body);
				}
			}
		}

		base.VisitSwitchExpression(switchExpr);
	}
}
