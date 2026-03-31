using ICSharpCode.Decompiler.CSharp.Syntax;
using ICSharpCode.Decompiler.CSharp.Transforms;

namespace GodotMonoDecomp.Transforms;

/// <summary>
/// Removes erroneous base._002Ector() calls that sometimes appear at the end of
/// constructor bodies in decompiled output.
/// </summary>
public class RemoveBogusBaseConstructorCalls : DepthFirstAstVisitor, IAstTransform
{
	ConstructorDeclaration? currentConstructor;

	public void Run(AstNode rootNode, TransformContext context)
	{
		rootNode.AcceptVisitor(this);
	}

	public override void VisitConstructorDeclaration(ConstructorDeclaration constructorDeclaration)
	{
		var previousConstructor = currentConstructor;
		currentConstructor = constructorDeclaration;
		try
		{
			base.VisitConstructorDeclaration(constructorDeclaration);
		}
		finally
		{
			currentConstructor = previousConstructor;
		}
	}

	public override void VisitInvocationExpression(InvocationExpression invocationExpression)
	{
		if (currentConstructor?.Body != null
			&& invocationExpression.Arguments.Count == 0
			&& invocationExpression.Target is MemberReferenceExpression memberReference
			&& memberReference.MemberName == "_002Ector"
			&& memberReference.Target is BaseReferenceExpression
			&& invocationExpression.Parent is ExpressionStatement expressionStatement
			&& expressionStatement.Parent == currentConstructor.Body
			&& currentConstructor.Body.Statements.LastOrDefault() == expressionStatement)
		{
			expressionStatement.Remove();
			return;
		}

		base.VisitInvocationExpression(invocationExpression);
	}
}
