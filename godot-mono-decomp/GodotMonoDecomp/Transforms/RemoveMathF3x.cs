// Copyright (c) 2011 AlphaSierraPapa for the SharpDevelop Team
//
// Permission is hereby granted, free of charge, to any person obtaining a copy of this
// software and associated documentation files (the "Software"), to deal in the Software
// without restriction, including without limitation the rights to use, copy, modify, merge,
// publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons
// to whom the Software is furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in all copies or
// substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED,
// INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR
// PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE
// FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR
// OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER
// DEALINGS IN THE SOFTWARE.

using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.CSharp.Syntax;
using ICSharpCode.Decompiler.CSharp.Transforms;

namespace GodotMonoDecomp.Transforms;

/// <summary>
///```c#
/// Replace calls to MathF.PI with Mathf.Pi in Godot 3.x
///```
/// </summary>
public class RemoveMathF3x : DepthFirstAstVisitor, IAstTransform
{
	public void Run(AstNode rootNode, TransformContext context)
	{
		rootNode.AcceptVisitor(this);
	}

	public static void ReplaceAndCopyAnnotations(Expression expression, Expression newExpression)
	{
		newExpression.CopyAnnotationsFrom(expression);
		expression.ReplaceWith(newExpression);
	}
	public override void VisitMemberReferenceExpression(MemberReferenceExpression mre)
	{
		if (mre.Target.ToString() == "MathF" && (mre.MemberName == "PI" || mre.MemberName == "E"))
		{
			var mathf = new IdentifierExpression("Mathf");
			ReplaceAndCopyAnnotations(mre.Target, mathf);
			if (mre.MemberName == "PI")
			{
				mre.MemberName = "Pi";
			}
			return;
		}
		base.VisitMemberReferenceExpression(mre);
	}
}
