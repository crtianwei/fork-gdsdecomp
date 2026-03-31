using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.CSharp.Syntax;
using ICSharpCode.Decompiler.CSharp.Transforms;

namespace GodotMonoDecomp.Transforms;

/// <summary>
/// Lifts a narrow set of constructor prelude initializers back to declaration initializers.
/// This targets list-prelude patterns that ILSpy lowers into SetCount/AsSpan/indexed writes.
/// </summary>
public class LiftCollectionInitializers : DepthFirstAstVisitor, IAstTransform
{
	private TypeDeclaration? currentType;

	public void Run(AstNode rootNode, TransformContext context)
	{
		rootNode.AcceptVisitor(this);
	}

	public override void VisitTypeDeclaration(TypeDeclaration typeDeclaration)
	{
		var previous = currentType;
		currentType = typeDeclaration;
		try
		{
			base.VisitTypeDeclaration(typeDeclaration);
		}
		finally
		{
			currentType = previous;
		}
	}

	public List<string> CollectMembersWithInitializers(TypeDeclaration typeDeclaration)
	{
		var participatingMembers = new List<string>();
		foreach (var member in typeDeclaration.Members)
		{
			if (member is FieldDeclaration field)
			{
				foreach (var variable in field.Variables)
				{
					if (!variable.AssignToken.IsNull || !variable.Initializer.IsNull)
					{
						foreach (var v in field.Variables)
						{
							participatingMembers.Add(variable.Name);
						}
						break;
					}
				}
			}
			else if (member is PropertyDeclaration property)
			{
				if (!property.Initializer.IsNull || !property.AssignToken.IsNull)
				{
					participatingMembers.Add(property.Name);
				}
			}
		}
		return participatingMembers.Distinct().ToList();
	}


	public override void VisitConstructorDeclaration(ConstructorDeclaration constructorDeclaration)
	{
		if (currentType == null || constructorDeclaration.Body.IsNull)
		{
			base.VisitConstructorDeclaration(constructorDeclaration);
			return;
		}

		bool isStaticConstructor = (constructorDeclaration.Modifiers & Modifiers.Static) == Modifiers.Static;
		var statements = constructorDeclaration.Body.Statements.ToArray();
		int boundaryIndex = isStaticConstructor ? statements.Length : FindConstructorBoundaryIndex(statements);
		if (!isStaticConstructor && boundaryIndex < 0)
		{
			base.VisitConstructorDeclaration(constructorDeclaration);
			return;
		}

		var memberMap = BuildMemberMap(currentType);
		var recoveredMemberOrder = CollectMembersWithInitializers(currentType);
		var matchedListInitByLocal = new Dictionary<string, Expression>(StringComparer.Ordinal);
		var statementsToRemove = new HashSet<Statement>();
		var localNames = CollectConstructorLocalNames(statements, boundaryIndex);

		int i = 0;
		while (i < boundaryIndex)
		{
			if (TryMatchListPrelude(statements, i, boundaryIndex, out var listMatch))
			{
				if (TryApplyInitializer(memberMap, listMatch.TargetMemberName, listMatch.InitializerExpression, isStaticConstructor))
				{
					recoveredMemberOrder.Add(listMatch.TargetMemberName);
					matchedListInitByLocal[listMatch.ListVariableName] = listMatch.InitializerExpression;
					foreach (var statement in listMatch.MatchedStatements)
					{
						statementsToRemove.Add(statement);
					}
				}

				i = listMatch.NextIndex;
				continue;
			}

			if (TryMatchListAddRangeSpreadPrelude(statements, i, boundaryIndex, out var listSpreadMatch))
			{
				if (TryApplyInitializer(memberMap, listSpreadMatch.TargetMemberName, listSpreadMatch.InitializerExpression, isStaticConstructor))
				{
					recoveredMemberOrder.Add(listSpreadMatch.TargetMemberName);
					matchedListInitByLocal[listSpreadMatch.ListVariableName] = listSpreadMatch.InitializerExpression;
					foreach (var statement in listSpreadMatch.MatchedStatements)
					{
						statementsToRemove.Add(statement);
					}
				}

				i = listSpreadMatch.NextIndex;
				continue;
			}

			if (TryMatchHashSetForeachSpreadPrelude(statements, i, boundaryIndex, out var hashSetSpreadMatch))
			{
				if (TryApplyInitializer(memberMap, hashSetSpreadMatch.TargetMemberName, hashSetSpreadMatch.InitializerExpression, isStaticConstructor))
				{
					recoveredMemberOrder.Add(hashSetSpreadMatch.TargetMemberName);
					matchedListInitByLocal[hashSetSpreadMatch.ListVariableName] = hashSetSpreadMatch.InitializerExpression;
					foreach (var statement in hashSetSpreadMatch.MatchedStatements)
					{
						statementsToRemove.Add(statement);
					}
				}

				i = hashSetSpreadMatch.NextIndex;
				continue;
			}

			if (TryMatchListSpreadBuilderWrapperAssignment(statements, i, boundaryIndex, currentType, out var wrappedSpreadMatch))
			{
				if (!ReferencesCtorLocal(wrappedSpreadMatch.InitializerExpression, localNames)
					&& TryApplyInitializer(memberMap, wrappedSpreadMatch.TargetMemberName, wrappedSpreadMatch.InitializerExpression, isStaticConstructor))
				{
					recoveredMemberOrder.Add(wrappedSpreadMatch.TargetMemberName);
					matchedListInitByLocal[wrappedSpreadMatch.ListVariableName] = wrappedSpreadMatch.InitializerExpression;
					foreach (var statement in wrappedSpreadMatch.MatchedStatements)
					{
						statementsToRemove.Add(statement);
					}
				}

				i = wrappedSpreadMatch.NextIndex;
				continue;
			}

			if (TryMatchConditionalTempAssignment(statements, i, boundaryIndex, currentType, out var conditionalTempMatch))
			{
				if (!ReferencesCtorLocal(conditionalTempMatch.InitializerExpression, localNames)
					&& TryApplyInitializer(memberMap, conditionalTempMatch.TargetMemberName, conditionalTempMatch.InitializerExpression, isStaticConstructor))
				{
					recoveredMemberOrder.Add(conditionalTempMatch.TargetMemberName);
					foreach (var statement in conditionalTempMatch.MatchedStatements)
					{
						statementsToRemove.Add(statement);
					}
				}

				i = conditionalTempMatch.NextIndex;
				continue;
			}

			if (TryMatchSimpleAssignment(statements[i], currentType, out var simpleTarget, out var simpleInitializer))
			{
				var candidateInitializer = simpleInitializer;
				if (ReferencesCtorLocal(candidateInitializer, localNames)
					&& TryRewriteInitializerWithRecoveredLocals(candidateInitializer, matchedListInitByLocal, out var rewrittenInitializer))
				{
					candidateInitializer = rewrittenInitializer;
				}
				candidateInitializer = SimplifyRedundantReadOnlyListCollectionWrappers(candidateInitializer);

				if (!ReferencesCtorLocal(candidateInitializer, localNames)
					&& TryApplyInitializer(memberMap, simpleTarget, candidateInitializer, isStaticConstructor))
				{
					recoveredMemberOrder.Add(simpleTarget);
					statementsToRemove.Add(statements[i]);
				}
				i++;
				continue;
			}

			// check if this is an assignment to a member at all; if so, break. We do not want to lift any more lest we change the ordering of the initializers
			if (isStaticConstructor || boundaryIndex < 0)
			{
				if (statements[i] is ExpressionStatement { Expression: AssignmentExpression { Operator: AssignmentOperatorType.Assign } assignment })
				{
					if (TryGetAssignedMemberName(assignment.Left, out var memberName) && memberMap.ContainsKey(memberName))
					{
						break;
					}
				}
			}

			i++;
		}

		if (!isStaticConstructor)
		{
			Statement? boundaryStatement = statements[boundaryIndex];
			if (TryBuildConstructorInitializer(
					constructorDeclaration,
					statements,
					boundaryIndex,
					boundaryStatement,
					matchedListInitByLocal,
					out var constructorInitializer,
					out var ctorPreludeStatements))
			{
				constructorDeclaration.Initializer = constructorInitializer;
				statementsToRemove.Add(boundaryStatement);
				foreach (var statement in ctorPreludeStatements)
				{
					statementsToRemove.Add(statement);
				}
			}
		}

		foreach (var statement in statements.Reverse())
		{
			if (statementsToRemove.Contains(statement))
			{
				statement.Remove();
			}
		}

		// Only run temp-noise cleanup when this transform actually consumed generated prelude statements.
		if (statementsToRemove.Count > 0)
		{
			CleanupLeadingTempNoise(constructorDeclaration);
		}

		// If lifting consumed generated ctor statements and left no body/initializer content,
		// drop the constructor declaration entirely.
		if (statementsToRemove.Count > 0
			&& constructorDeclaration.Body.Statements.Count == 0
			&& constructorDeclaration.Initializer.IsNull)
		{
			constructorDeclaration.Remove();
			ReorderRecoveredMembers(currentType, recoveredMemberOrder, memberMap);
			return;
		}

		ReorderRecoveredMembers(currentType, recoveredMemberOrder, memberMap);
	}

	private static int FindConstructorBoundaryIndex(IReadOnlyList<Statement> statements)
	{
		for (int i = 0; i < statements.Count; i++)
		{
			if (TryGetCtorInvocation(statements[i], out _, out _))
			{
				return i;
			}
		}

		return -1;
	}

	private static bool TryGetCtorInvocation(
		Statement statement,
		out ConstructorInitializerType initializerType,
		out InvocationExpression invocation)
	{
		initializerType = ConstructorInitializerType.Any;
		invocation = null!;
		if (statement is not ExpressionStatement { Expression: InvocationExpression invocationExpression })
		{
			return false;
		}

		if (invocationExpression.Target is not MemberReferenceExpression memberReference
			|| !string.Equals(memberReference.MemberName, "_002Ector", StringComparison.Ordinal))
		{
			return false;
		}

		if (memberReference.Target is BaseReferenceExpression)
		{
			initializerType = ConstructorInitializerType.Base;
		}
		else if (memberReference.Target is ThisReferenceExpression)
		{
			initializerType = ConstructorInitializerType.This;
		}
		else
		{
			return false;
		}

		invocation = invocationExpression;
		return true;
	}

	private static Dictionary<string, EntityDeclaration> BuildMemberMap(TypeDeclaration typeDeclaration)
	{
		var map = new Dictionary<string, EntityDeclaration>(StringComparer.Ordinal);
		foreach (var member in typeDeclaration.Members)
		{
			if (member is FieldDeclaration field)
			{
				foreach (var variable in field.Variables)
				{
					map.TryAdd(variable.Name, field);
				}
			}
			else if (member is PropertyDeclaration property)
			{
				map.TryAdd(property.Name, property);
			}
		}

		return map;
	}

	private static bool TryApplyInitializer(
		Dictionary<string, EntityDeclaration> memberMap,
		string memberName,
		Expression initializer,
		bool expectStaticMember)
	{
		if (!memberMap.TryGetValue(memberName, out var member))
		{
			return false;
		}
		if (IsStaticMember(member) != expectStaticMember)
		{
			return false;
		}

		switch (member)
		{
			case FieldDeclaration field:
				var variable = field.Variables.FirstOrDefault(v => string.Equals(v.Name, memberName, StringComparison.Ordinal));
				if (variable == null)
				{
					return false;
				}
				variable.Initializer = CopyInstructionAnnotationsFromSource(initializer.Clone(), initializer);
				return true;
			case PropertyDeclaration property:
				if (!property.IsAutomaticProperty)
				{
					return false;
				}
				property.Initializer = CopyInstructionAnnotationsFromSource(initializer.Clone(), initializer);
				return true;
			default:
				return false;
		}
	}

	private static T CopyInstructionAnnotationsFromSource<T>(T target, AstNode source)
		where T : AstNode
	{
		target.CopyInstructionsFrom(source);
		return target;
	}

	private static T CopyInstructionAnnotationsFromSources<T>(
		T target,
		IEnumerable<Statement>? statementSources = null,
		IEnumerable<Expression>? expressionSources = null)
		where T : AstNode
	{
		if (statementSources != null)
		{
			foreach (var statement in statementSources)
			{
				target.CopyInstructionsFrom(statement);
			}
		}

		if (expressionSources != null)
		{
			foreach (var expression in expressionSources)
			{
				target.CopyInstructionsFrom(expression);
			}
		}

		return target;
	}

	private static bool IsStaticMember(EntityDeclaration member)
	{
		return (member.Modifiers & Modifiers.Static) == Modifiers.Static;
	}

	private static HashSet<string> CollectConstructorLocalNames(IReadOnlyList<Statement> statements, int boundaryIndex)
	{
		var localNames = new HashSet<string>(StringComparer.Ordinal);
		for (int i = 0; i < boundaryIndex && i < statements.Count; i++)
		{
			if (statements[i] is not VariableDeclarationStatement declaration)
			{
				continue;
			}

			foreach (var variable in declaration.Variables)
			{
				if (!string.IsNullOrEmpty(variable.Name))
				{
					localNames.Add(variable.Name);
				}
			}
		}

		return localNames;
	}

	private static bool ReferencesCtorLocal(Expression expression, HashSet<string> localNames)
	{
		if (localNames.Count == 0)
		{
			return false;
		}

		if (expression is IdentifierExpression { Identifier: var identifier }
			&& localNames.Contains(identifier))
		{
			return true;
		}

		return expression.Descendants.OfType<IdentifierExpression>()
			.Any(id => localNames.Contains(id.Identifier));
	}

	private static bool TryMatchSimpleAssignment(Statement statement, TypeDeclaration currentType, out string memberName, out Expression initializer)
	{
		memberName = string.Empty;
		initializer = Expression.Null;
		if (statement is not ExpressionStatement { Expression: AssignmentExpression { Operator: AssignmentOperatorType.Assign } assignment })
		{
			return false;
		}

		if (!TryGetAssignedMemberName(assignment.Left, out memberName))
		{
			return false;
		}

		// Keep conservative: only side-effect free literal/create/delegate expressions.
		if (IsAllowedLiftInitializerExpression(assignment.Right, currentType))
		{
			initializer = assignment.Right;
			return true;
		}

		return false;
	}

	private static bool TryMatchConditionalTempAssignment(
		IReadOnlyList<Statement> statements,
		int startIndex,
		int boundaryIndex,
		TypeDeclaration currentType,
		out ConditionalTempAssignmentMatch match)
	{
		match = default;
		if (startIndex + 2 >= boundaryIndex
			|| statements[startIndex] is not VariableDeclarationStatement tempDecl
			|| tempDecl.Variables.Count != 1
			|| tempDecl.Variables.FirstOrDefault() is not { } tempVar
			|| !tempVar.Initializer.IsNull)
		{
			return false;
		}

		string tempVarName = tempVar.Name;
		if (string.IsNullOrEmpty(tempVarName)
			|| statements[startIndex + 1] is not IfElseStatement ifElse
			|| ifElse.TrueStatement is not BlockStatement trueBlock
			|| ifElse.FalseStatement is not BlockStatement falseBlock
			|| !TryMatchConditionalTempBranch(trueBlock, tempVarName, currentType, out var trueInitializer)
			|| !TryMatchConditionalTempBranch(falseBlock, tempVarName, currentType, out var falseInitializer))
		{
			return false;
		}

		if (!TryMatchTerminalAssignment(statements[startIndex + 2], tempVarName, out var memberName))
		{
			return false;
		}

		var conditionalInitializer = new ConditionalExpression(
			ifElse.Condition.Clone(),
			trueInitializer.Clone(),
			falseInitializer.Clone());
		CopyInstructionAnnotationsFromSources(
			conditionalInitializer,
			new List<Statement> { statements[startIndex], statements[startIndex + 1], statements[startIndex + 2] },
			new[] { ifElse.Condition, trueInitializer, falseInitializer });
		match = new ConditionalTempAssignmentMatch(
			memberName,
			conditionalInitializer,
			new List<Statement> { statements[startIndex], statements[startIndex + 1], statements[startIndex + 2] },
			startIndex + 3
		);
		return true;
	}

	private static bool TryMatchConditionalTempBranch(
		BlockStatement branchBlock,
		string tempVarName,
		TypeDeclaration currentType,
		out Expression branchInitializer)
	{
		branchInitializer = Expression.Null;
		if (branchBlock.Statements.Count != 2
			|| branchBlock.Statements.FirstOrDefault() is not VariableDeclarationStatement localDecl
			|| localDecl.Variables.Count != 1
			|| localDecl.Variables.FirstOrDefault() is not { } localVar
			|| localVar.Initializer is not { } localInitializer
			|| localInitializer.IsNull
			|| !IsAllowedLiftInitializerExpression(localInitializer, currentType)
			|| branchBlock.Statements.LastOrDefault() is not ExpressionStatement
			{
				Expression: AssignmentExpression
				{
					Operator: AssignmentOperatorType.Assign,
					Left: IdentifierExpression { Identifier: var assignedTempVarName },
					Right: IdentifierExpression { Identifier: var assignedLocalName }
				}
			}
			|| !string.Equals(assignedTempVarName, tempVarName, StringComparison.Ordinal)
			|| !string.Equals(assignedLocalName, localVar.Name, StringComparison.Ordinal))
		{
			return false;
		}

		branchInitializer = localInitializer.Clone();
		return true;
	}

	private static bool TryRewriteInitializerWithRecoveredLocals(
		Expression initializer,
		IReadOnlyDictionary<string, Expression> recoveredInitializersByLocal,
		out Expression rewritten)
	{
		rewritten = initializer;
		if (recoveredInitializersByLocal.Count == 0)
		{
			return false;
		}

		var clone = initializer.Clone();
		bool changed = false;
		if (clone is IdentifierExpression { Identifier: var rootIdentifier }
			&& recoveredInitializersByLocal.TryGetValue(rootIdentifier, out var rootReplacement))
		{
			clone = rootReplacement.Clone();
			changed = true;
		}

		foreach (var identifier in clone.Descendants.OfType<IdentifierExpression>().ToArray())
		{
			if (!recoveredInitializersByLocal.TryGetValue(identifier.Identifier, out var replacement))
			{
				continue;
			}

			var replacementClone = CopyInstructionAnnotationsFromSources(
				replacement.Clone(),
				expressionSources: new[] { identifier, replacement });
			identifier.ReplaceWith(replacementClone);
			changed = true;
		}

		if (!changed)
		{
			return false;
		}

		rewritten = clone;
		return true;
	}

	private static Expression SimplifyRedundantReadOnlyListCollectionWrappers(Expression initializer)
	{
		Expression expression = initializer.Clone();

		while (expression is ObjectCreateExpression rootCreate
			&& TryUnwrapReadOnlyListOfListCollection(rootCreate, out var rootReplacement))
		{
			expression = CopyInstructionAnnotationsFromSources(
				rootReplacement.Clone(),
				expressionSources: new[] { rootCreate, rootReplacement });
		}

		foreach (var objectCreate in expression.Descendants.OfType<ObjectCreateExpression>().ToArray())
		{
			if (TryUnwrapReadOnlyListOfListCollection(objectCreate, out var replacement))
			{
				var replacementClone = CopyInstructionAnnotationsFromSources(
					replacement.Clone(),
					expressionSources: new[] { objectCreate, replacement });
				objectCreate.ReplaceWith(replacementClone);
			}
		}

		return expression;
	}

	private static bool TryUnwrapReadOnlyListOfListCollection(ObjectCreateExpression readOnlyListCreate, out Expression collectionExpression)
	{
		collectionExpression = Expression.Null;
		if (!IsTypeIdentifier(readOnlyListCreate.Type, "_003C_003Ez__ReadOnlyList")
			|| readOnlyListCreate.Arguments.Count != 1
			|| readOnlyListCreate.Arguments.FirstOrDefault() is not ObjectCreateExpression listCreate
			|| !IsTypeIdentifier(listCreate.Type, "List")
			|| listCreate.Arguments.Count != 1
			|| listCreate.Arguments.FirstOrDefault() is not ArrayInitializerExpression arrayInitializer
			|| arrayInitializer.Annotation<CollectionExpressionArrayAnnotation>() == null)
		{
			return false;
		}

		collectionExpression = arrayInitializer.Clone();
		return true;
	}

	private static bool IsTypeIdentifier(AstType type, string identifier)
	{
		return type is SimpleType { Identifier: var typeIdentifier }
			&& string.Equals(typeIdentifier, identifier, StringComparison.Ordinal);
	}

	private static bool IsAllowedMemberReferenceOrInvocation(Expression expression, TypeDeclaration currentType)
	{
		if (expression is not MemberReferenceExpression && expression is not InvocationExpression)
		{
			return false;
		}

		var typeMemberNames = CollectTypeMemberNames(currentType);
		if (ReferencesCurrentTypeMembers(expression, currentType, typeMemberNames))
		{
			return false;
		}

		// Unqualified invocation can bind to a ctor-local function or type method; keep this conservative.
		if (expression is InvocationExpression { Target: IdentifierExpression })
		{
			return false;
		}

		return true;
	}

	private static HashSet<string> CollectTypeMemberNames(TypeDeclaration currentType)
	{
		var names = new HashSet<string>(StringComparer.Ordinal);
		foreach (var member in currentType.Members)
		{
			switch (member)
			{
				case FieldDeclaration field:
					foreach (var variable in field.Variables)
					{
						if (!string.IsNullOrEmpty(variable.Name))
						{
							names.Add(variable.Name);
						}
					}
					break;
				case PropertyDeclaration property:
					if (!string.IsNullOrEmpty(property.Name))
					{
						names.Add(property.Name);
					}
					break;
				case MethodDeclaration method:
					if (!string.IsNullOrEmpty(method.Name))
					{
						names.Add(method.Name);
					}
					break;
				case TypeDeclaration nestedType:
					if (!string.IsNullOrEmpty(nestedType.Name))
					{
						names.Add(nestedType.Name);
					}
					break;
			}
		}

		return names;
	}

	private static bool ReferencesCurrentTypeMembers(Expression expression, TypeDeclaration currentType, HashSet<string> typeMemberNames)
	{
		if (expression is ThisReferenceExpression
			|| expression is BaseReferenceExpression)
		{
			return true;
		}

		if (expression is IdentifierExpression { Identifier: var identifier }
			&& typeMemberNames.Contains(identifier))
		{
			return true;
		}

		if (expression is MemberReferenceExpression memberReference
			&& memberReference.Target is IdentifierExpression { Identifier: var targetIdentifier }
			&& string.Equals(targetIdentifier, currentType.Name, StringComparison.Ordinal))
		{
			return true;
		}

		if (expression is MemberReferenceExpression
			{
				Target: TypeReferenceExpression
				{
					Type: SimpleType { Identifier: var targetTypeIdentifier }
				}
			}
			&& string.Equals(targetTypeIdentifier, currentType.Name, StringComparison.Ordinal))
		{
			return true;
		}

		if (expression.Descendants.OfType<ThisReferenceExpression>().Any()
			|| expression.Descendants.OfType<BaseReferenceExpression>().Any())
		{
			return true;
		}

		if (expression.Descendants.OfType<IdentifierExpression>()
			.Any(id => typeMemberNames.Contains(id.Identifier)))
		{
			return true;
		}

		if (expression.Descendants.OfType<MemberReferenceExpression>()
			.Any(member => member.Target is IdentifierExpression { Identifier: var id }
				&& string.Equals(id, currentType.Name, StringComparison.Ordinal)))
		{
			return true;
		}

		return expression.Descendants.OfType<MemberReferenceExpression>()
			.Any(member => member.Target is TypeReferenceExpression { Type: SimpleType { Identifier: var id } }
				&& string.Equals(id, currentType.Name, StringComparison.Ordinal));
	}

	private static bool IsAllowedLiftInitializerExpression(Expression expression, TypeDeclaration currentType)
	{
		return IsAllowedSimpleInitializerExpression(expression)
			|| IsAllowedMemberReferenceOrInvocation(expression, currentType);
	}

	private static bool IsAllowedSimpleInitializerExpression(Expression expression)
	{
		return expression is PrimitiveExpression
			|| expression is NullReferenceExpression
			|| expression is ObjectCreateExpression
			|| expression is LambdaExpression
			|| expression is AnonymousMethodExpression;
	}

	private static bool TryGetAssignedMemberName(Expression left, out string memberName)
	{
		memberName = string.Empty;
		if (left is IdentifierExpression ident)
		{
			memberName = ident.Identifier;
			return true;
		}

		if (left is MemberReferenceExpression memberRef
			&& memberRef.Target is ThisReferenceExpression or IdentifierExpression)
		{
			memberName = memberRef.MemberName;
			return true;
		}

		return false;
	}

	private static bool TryMatchListPrelude(
		IReadOnlyList<Statement> statements,
		int startIndex,
		int boundaryIndex,
		out ListPreludeMatch match)
	{
		match = default;
		if (startIndex + 3 >= boundaryIndex)
		{
			return false;
		}

		if (statements[startIndex] is not VariableDeclarationStatement listDecl
			|| listDecl.Variables.Count != 1
			|| listDecl.Variables.FirstOrDefault() is not { } listVariable
			|| listVariable.Initializer is not ObjectCreateExpression listCreate)
		{
			return false;
		}

		string listVarName = listVariable.Name;
		if (string.IsNullOrEmpty(listVarName))
		{
			return false;
		}

		if (!IsCollectionsMarshalCall(statements[startIndex + 1], "SetCount", listVarName))
		{
			return false;
		}

		if (statements[startIndex + 2] is not VariableDeclarationStatement spanDecl
			|| spanDecl.Variables.Count != 1
			|| spanDecl.Variables.FirstOrDefault() is not { } spanVariable
			|| spanVariable.Initializer is not InvocationExpression spanInit
			|| !IsCollectionsMarshalAsSpanCall(spanInit, listVarName))
		{
			return false;
		}

		string spanVarName = spanVariable.Name;
		var values = new List<Expression>();
		var matchedStatements = new List<Statement> { statements[startIndex], statements[startIndex + 1], statements[startIndex + 2] };
		string targetMemberName = string.Empty;
		int i = startIndex + 3;
		for (; i < boundaryIndex; i++)
		{
			var statement = statements[i];
			if (TryMatchTerminalAssignment(statement, listVarName, out targetMemberName))
			{
				matchedStatements.Add(statement);
				break;
			}

			if (TryCollectSpanAssignmentValue(statement, spanVarName, out var value))
			{
				values.Add(value.Clone());
				matchedStatements.Add(statement);
				continue;
			}

			if (TryCollectNestedSpanAssignmentValue(
					statements,
					i,
					boundaryIndex,
					spanVarName,
					out var nestedValue,
					out var nextIndex,
					out var nestedMatchedStatements))
			{
				values.Add(nestedValue.Clone());
				matchedStatements.AddRange(nestedMatchedStatements);
				i = nextIndex - 1;
				continue;
			}

			if (IsAllowedPreludeNoise(statement))
			{
				continue;
			}

			return false;
		}

		if (i >= boundaryIndex || string.IsNullOrEmpty(targetMemberName))
		{
			return false;
		}

		var arrayInitializer = CopyInstructionAnnotationsFromSources(
			new ArrayInitializerExpression(values),
			matchedStatements,
			values);
		var collectionInitializer = new ObjectCreateExpression(listDecl.Type.Clone())
		{
			Initializer = arrayInitializer
		};
		CopyInstructionAnnotationsFromSources(collectionInitializer, matchedStatements, values);
		match = new ListPreludeMatch(
			listVarName,
			targetMemberName,
			collectionInitializer,
			matchedStatements,
			i + 1
		);
		return true;
	}

	private static bool TryMatchListAddRangeSpreadPrelude(
		IReadOnlyList<Statement> statements,
		int startIndex,
		int boundaryIndex,
		out ListPreludeMatch match)
	{
		match = default;
		if (startIndex + 2 >= boundaryIndex)
		{
			return false;
		}

		if (statements[startIndex] is not VariableDeclarationStatement listDecl
			|| listDecl.Variables.Count != 1
			|| listDecl.Variables.FirstOrDefault() is not { } listVariable
			|| listVariable.Initializer is not ObjectCreateExpression listCreate
			|| !IsGenericCollectionType(listCreate.Type, "List"))
		{
			return false;
		}

		string listVarName = listVariable.Name;
		if (string.IsNullOrEmpty(listVarName))
		{
			return false;
		}

		var segments = new List<SpreadSegment>();
		var matchedStatements = new List<Statement> { statements[startIndex] };
		string targetMemberName = string.Empty;
		int i = startIndex + 1;
		for (; i < boundaryIndex; i++)
		{
			var statement = statements[i];
			if (TryMatchTerminalAssignment(statement, listVarName, out targetMemberName))
			{
				matchedStatements.Add(statement);
				break;
			}

			if (TryMatchCollectionSpreadOperation(statement, listVarName, "AddRange", out var spreadSource))
			{
				segments.Add(SpreadSegment.FromRange(spreadSource.Clone()));
				matchedStatements.Add(statement);
				continue;
			}

			if (TryMatchCollectionSpreadOperation(statement, listVarName, "Add", out var addValue))
			{
				segments.Add(SpreadSegment.FromValue(addValue.Clone()));
				matchedStatements.Add(statement);
				continue;
			}

			if (IsAllowedPreludeNoise(statement))
			{
				continue;
			}

			return false;
		}

		if (segments.Count == 0 || i >= boundaryIndex || string.IsNullOrEmpty(targetMemberName))
		{
			return false;
		}

		if (!TryBuildCollectionFromSpreadSegments(listDecl.Type, segments, matchedStatements, out var collectionInitializer))
		{
			return false;
		}

		match = new ListPreludeMatch(
			listVarName,
			targetMemberName,
			collectionInitializer,
			matchedStatements,
			i + 1
		);
		return true;
	}

	private static bool TryMatchHashSetForeachSpreadPrelude(
		IReadOnlyList<Statement> statements,
		int startIndex,
		int boundaryIndex,
		out ListPreludeMatch match)
	{
		match = default;
		if (startIndex + 2 >= boundaryIndex)
		{
			return false;
		}

		if (statements[startIndex] is not VariableDeclarationStatement setDecl
			|| setDecl.Variables.Count != 1
			|| setDecl.Variables.FirstOrDefault() is not { } setVariable
			|| setVariable.Initializer is not ObjectCreateExpression setCreate
			|| !IsGenericCollectionType(setCreate.Type, "HashSet"))
		{
			return false;
		}

		string setVarName = setVariable.Name;
		if (string.IsNullOrEmpty(setVarName))
		{
			return false;
		}

		var segments = new List<SpreadSegment>();
		var matchedStatements = new List<Statement> { statements[startIndex] };
		string targetMemberName = string.Empty;
		int i = startIndex + 1;
		for (; i < boundaryIndex; i++)
		{
			var statement = statements[i];
			if (TryMatchTerminalAssignment(statement, setVarName, out targetMemberName))
			{
				matchedStatements.Add(statement);
				break;
			}

			if (TryMatchHashSetSpreadForeach(statement, setVarName, out var spreadEnumerable))
			{
				segments.Add(SpreadSegment.FromRange(spreadEnumerable.Clone()));
				matchedStatements.Add(statement);
				continue;
			}

			if (TryMatchCollectionSpreadOperation(statement, setVarName, "Add", out var addValue))
			{
				segments.Add(SpreadSegment.FromValue(addValue.Clone()));
				matchedStatements.Add(statement);
				continue;
			}

			if (IsAllowedPreludeNoise(statement))
			{
				continue;
			}

			return false;
		}

		if (segments.Count == 0 || i >= boundaryIndex || string.IsNullOrEmpty(targetMemberName))
		{
			return false;
		}

		if (!TryBuildCollectionFromSpreadSegments(setDecl.Type, segments, matchedStatements, out var collectionInitializer))
		{
			return false;
		}

		match = new ListPreludeMatch(
			setVarName,
			targetMemberName,
			collectionInitializer,
			matchedStatements,
			i + 1
		);
		return true;
	}

	private static bool TryMatchListSpreadBuilderWrapperAssignment(
		IReadOnlyList<Statement> statements,
		int startIndex,
		int boundaryIndex,
		TypeDeclaration currentType,
		out ListPreludeMatch match)
	{
		match = default;
		if (startIndex + 2 >= boundaryIndex)
		{
			return false;
		}

		if (statements[startIndex] is not VariableDeclarationStatement listDecl
			|| listDecl.Variables.Count != 1
			|| listDecl.Variables.FirstOrDefault() is not { } listVariable
			|| listVariable.Initializer is not ObjectCreateExpression listCreate
			|| !IsGenericCollectionType(listCreate.Type, "List"))
		{
			return false;
		}

		string listVarName = listVariable.Name;
		if (string.IsNullOrEmpty(listVarName))
		{
			return false;
		}

		var segments = new List<SpreadSegment>();
		var matchedStatements = new List<Statement> { statements[startIndex] };
		int i = startIndex + 1;
		for (; i < boundaryIndex; i++)
		{
			var statement = statements[i];
			if (TryMatchCollectionSpreadOperation(statement, listVarName, "AddRange", out var spreadSource))
			{
				segments.Add(SpreadSegment.FromRange(spreadSource.Clone()));
				matchedStatements.Add(statement);
				continue;
			}

			if (TryMatchCollectionSpreadOperation(statement, listVarName, "Add", out var addValue))
			{
				segments.Add(SpreadSegment.FromValue(addValue.Clone()));
				matchedStatements.Add(statement);
				continue;
			}

			if (IsAllowedPreludeNoise(statement))
			{
				continue;
			}

			break;
		}

		if (segments.Count == 0 || i >= boundaryIndex)
		{
			return false;
		}

		if (!TryBuildCollectionFromSpreadSegments(listDecl.Type, segments, matchedStatements, out var listBuilderInitializer))
		{
			return false;
		}

		if (!TryMatchSimpleAssignment(statements[i], currentType, out var targetMemberName, out var assignmentInitializer))
		{
			return false;
		}

		var replacementMap = new Dictionary<string, Expression>(StringComparer.Ordinal)
		{
			[listVarName] = listBuilderInitializer
		};
		if (!TryRewriteInitializerWithRecoveredLocals(assignmentInitializer, replacementMap, out var rewrittenInitializer))
		{
			return false;
		}
		rewrittenInitializer = SimplifyRedundantReadOnlyListCollectionWrappers(rewrittenInitializer);

		matchedStatements.Add(statements[i]);
		match = new ListPreludeMatch(
			listVarName,
			targetMemberName,
			rewrittenInitializer,
			matchedStatements,
			i + 1
		);
		return true;
	}

	private static bool IsGenericCollectionType(AstType type, string expectedIdentifier)
	{
		return type is SimpleType { Identifier: var identifier } simpleType
			&& string.Equals(identifier, expectedIdentifier, StringComparison.Ordinal)
			&& simpleType.TypeArguments.Count == 1;
	}

	private static bool TryMatchCollectionSpreadOperation(
		Statement statement,
		string collectionVariableName,
		string methodName,
		out Expression argument)
	{
		argument = Expression.Null;
		if (statement is not ExpressionStatement { Expression: InvocationExpression invocation }
			|| invocation.Target is not MemberReferenceExpression memberRef
			|| !string.Equals(memberRef.MemberName, methodName, StringComparison.Ordinal)
			|| memberRef.Target is not IdentifierExpression { Identifier: var targetIdentifier }
			|| !string.Equals(targetIdentifier, collectionVariableName, StringComparison.Ordinal))
		{
			return false;
		}

		if (invocation.Arguments.Count != 1)
		{
			return false;
		}

		argument = invocation.Arguments.First().Clone();
		return true;
	}

	private static bool TryMatchHashSetSpreadForeach(
		Statement statement,
		string setVariableName,
		out Expression spreadEnumerable)
	{
		spreadEnumerable = Expression.Null;
		if (statement is not ForeachStatement foreachStatement
			|| foreachStatement.EmbeddedStatement is not BlockStatement foreachBlock
			|| foreachBlock.Statements.Count != 1
			|| foreachBlock.Statements.FirstOrDefault() is not ExpressionStatement { Expression: InvocationExpression addInvocation }
			|| addInvocation.Target is not MemberReferenceExpression addTarget
			|| !string.Equals(addTarget.MemberName, "Add", StringComparison.Ordinal)
			|| addTarget.Target is not IdentifierExpression { Identifier: var addTargetIdentifier }
			|| !string.Equals(addTargetIdentifier, setVariableName, StringComparison.Ordinal)
			|| addInvocation.Arguments.Count != 1
			|| addInvocation.Arguments.FirstOrDefault() is not IdentifierExpression { Identifier: var addedIdentifier }
			|| foreachStatement.VariableDesignation is not SingleVariableDesignation { Identifier: var loopIdentifier }
			|| !string.Equals(addedIdentifier, loopIdentifier, StringComparison.Ordinal))
		{
			return false;
		}

		spreadEnumerable = foreachStatement.InExpression.Clone();
		return true;
	}

	private static bool TryBuildCollectionFromSpreadSegments(
		AstType collectionType,
		IReadOnlyList<SpreadSegment> segments,
		IReadOnlyList<Statement> matchedStatements,
		out Expression initializer)
	{
		initializer = Expression.Null;
		if (segments.Count == 0)
		{
			return false;
		}

		var elements = new List<Expression>();
		foreach (var segment in segments)
		{
			var element = segment.Expression.Clone();
			if (segment.IsSpread)
			{
				element.AddAnnotation(CollectionExpressionSpreadElementAnnotation.Instance);
			}
			elements.Add(element);
		}

		var collectionExpression = new ArrayInitializerExpression(elements);
		collectionExpression.AddAnnotation(CollectionExpressionArrayAnnotation.Instance);
		CopyInstructionAnnotationsFromSources(collectionExpression, matchedStatements, segments.Select(segment => segment.Expression));
		initializer = new ObjectCreateExpression(collectionType.Clone(), collectionExpression);
		CopyInstructionAnnotationsFromSources(initializer, matchedStatements, segments.Select(segment => segment.Expression));
		return true;
	}

	private static bool IsCollectionsMarshalCall(Statement statement, string methodName, string firstArgIdentifier)
	{
		if (statement is not ExpressionStatement { Expression: InvocationExpression invocation }
			|| invocation.Target is not MemberReferenceExpression memberRef
			|| !string.Equals(memberRef.MemberName, methodName, StringComparison.Ordinal)
			|| !IsCollectionsMarshalTarget(memberRef.Target))
		{
			return false;
		}

		if (invocation.Arguments.FirstOrDefault() is not IdentifierExpression { Identifier: var firstArg })
		{
			return false;
		}

		return string.Equals(firstArg, firstArgIdentifier, StringComparison.Ordinal);
	}

	private static bool IsCollectionsMarshalAsSpanCall(InvocationExpression invocation, string listVarName)
	{
		if (invocation.Target is not MemberReferenceExpression memberRef
			|| !string.Equals(memberRef.MemberName, "AsSpan", StringComparison.Ordinal)
			|| !IsCollectionsMarshalTarget(memberRef.Target))
		{
			return false;
		}

		return invocation.Arguments.FirstOrDefault() is IdentifierExpression { Identifier: var firstArg }
			&& string.Equals(firstArg, listVarName, StringComparison.Ordinal);
	}

	private static bool IsCollectionsMarshalTarget(Expression target)
	{
		if (target is IdentifierExpression { Identifier: "CollectionsMarshal" })
		{
			return true;
		}

		if (target is TypeReferenceExpression { Type: SimpleType { Identifier: "CollectionsMarshal" } })
		{
			return true;
		}

		return false;
	}

	private static bool TryCollectSpanAssignmentValue(Statement statement, string spanVarName, out Expression value)
	{
		value = Expression.Null;
		if (statement is not ExpressionStatement { Expression: AssignmentExpression { Operator: AssignmentOperatorType.Assign } assignment })
		{
			return false;
		}

		if (assignment.Left is IndexerExpression
			{
				Target: IdentifierExpression { Identifier: var targetName }
			}
			&& string.Equals(targetName, spanVarName, StringComparison.Ordinal))
		{
			value = assignment.Right;
			return true;
		}

		return false;
	}

	private static bool TryCollectNestedSpanAssignmentValue(
		IReadOnlyList<Statement> statements,
		int startIndex,
		int boundaryIndex,
		string outerSpanVarName,
		out Expression value,
		out int nextIndex,
		out List<Statement> matchedStatements)
	{
		value = Expression.Null;
		nextIndex = -1;
		matchedStatements = [];
		if (startIndex + 1 >= boundaryIndex)
		{
			return false;
		}

		if (statements[startIndex] is not VariableDeclarationStatement refSlotDecl
			|| refSlotDecl.Variables.Count != 1
			|| refSlotDecl.Variables.FirstOrDefault() is not { } refSlotVar
			|| refSlotVar.Initializer is not DirectionExpression
			{
				FieldDirection: FieldDirection.Ref,
				Expression: IndexerExpression
				{
					Target: IdentifierExpression { Identifier: var slotTargetSpan }
				}
			}
			|| !string.Equals(slotTargetSpan, outerSpanVarName, StringComparison.Ordinal))
		{
			return false;
		}

		string slotVariableName = refSlotVar.Name;
		if (string.IsNullOrEmpty(slotVariableName))
		{
			return false;
		}

		if (statements[startIndex + 1] is not VariableDeclarationStatement objectDecl
			|| objectDecl.Variables.Count != 1
			|| objectDecl.Variables.FirstOrDefault() is not { } objectVar
			|| objectVar.Initializer is not ObjectCreateExpression objectCreate)
		{
			return false;
		}

		string objectVariableName = objectVar.Name;
		if (string.IsNullOrEmpty(objectVariableName))
		{
			return false;
		}

		var objectValue = (ObjectCreateExpression)objectCreate.Clone();
		objectValue.Initializer ??= new ArrayInitializerExpression();
		var assignedMembers = new HashSet<string>(StringComparer.Ordinal);
		foreach (var named in objectValue.Initializer.Elements.OfType<NamedExpression>())
		{
			assignedMembers.Add(named.Name);
		}

		matchedStatements.Add(statements[startIndex]);
		matchedStatements.Add(statements[startIndex + 1]);

		for (int i = startIndex + 2; i < boundaryIndex; i++)
		{
			var statement = statements[i];
			if (TryMatchRefSlotAssignment(statement, slotVariableName, objectVariableName))
			{
				matchedStatements.Add(statement);
				value = objectValue;
				nextIndex = i + 1;
				return true;
			}

			if (TryMatchObjectMemberListPrelude(
					statements,
					i,
					boundaryIndex,
					objectVariableName,
					out var listMemberName,
					out var listMemberValue,
					out var listMatchedStatements,
					out var listNextIndex))
			{
				if (!assignedMembers.Add(listMemberName))
				{
					return false;
				}

				AddObjectMemberInitializer(objectValue, listMemberName, listMemberValue);
				matchedStatements.AddRange(listMatchedStatements);
				i = listNextIndex - 1;
				continue;
			}

			if (TryMatchObjectMemberSimpleAssignment(statement, objectVariableName, out var memberName, out var memberValue))
			{
				if (!assignedMembers.Add(memberName))
				{
					return false;
				}

				AddObjectMemberInitializer(objectValue, memberName, memberValue);
				matchedStatements.Add(statement);
				continue;
			}

			if (IsAllowedPreludeNoise(statement))
			{
				matchedStatements.Add(statement);
				continue;
			}

			return false;
		}

		return false;
	}

	private static bool TryMatchObjectMemberListPrelude(
		IReadOnlyList<Statement> statements,
		int startIndex,
		int boundaryIndex,
		string objectVariableName,
		out string memberName,
		out Expression initializerValue,
		out List<Statement> matchedStatements,
		out int nextIndex)
	{
		memberName = string.Empty;
		initializerValue = Expression.Null;
		matchedStatements = [];
		nextIndex = -1;
		if (startIndex + 3 >= boundaryIndex)
		{
			return false;
		}

		if (statements[startIndex] is not VariableDeclarationStatement listDecl
			|| listDecl.Variables.Count != 1
			|| listDecl.Variables.FirstOrDefault() is not { } listVariable
			|| listVariable.Initializer is not ObjectCreateExpression)
		{
			return false;
		}

		string listVarName = listVariable.Name;
		if (string.IsNullOrEmpty(listVarName))
		{
			return false;
		}

		if (!IsCollectionsMarshalCall(statements[startIndex + 1], "SetCount", listVarName))
		{
			return false;
		}

		if (statements[startIndex + 2] is not VariableDeclarationStatement spanDecl
			|| spanDecl.Variables.Count != 1
			|| spanDecl.Variables.FirstOrDefault() is not { } spanVariable
			|| spanVariable.Initializer is not InvocationExpression spanInit
			|| !IsCollectionsMarshalAsSpanCall(spanInit, listVarName))
		{
			return false;
		}

		string spanVarName = spanVariable.Name;
		var values = new List<Expression>();
		matchedStatements.Add(statements[startIndex]);
		matchedStatements.Add(statements[startIndex + 1]);
		matchedStatements.Add(statements[startIndex + 2]);

		for (int i = startIndex + 3; i < boundaryIndex; i++)
		{
			var statement = statements[i];
			if (TryMatchTerminalObjectMemberAssignment(statement, listVarName, objectVariableName, out memberName))
			{
				matchedStatements.Add(statement);
				var arrayInitializer = CopyInstructionAnnotationsFromSources(
					new ArrayInitializerExpression(values),
					matchedStatements,
					values);
				initializerValue = new ObjectCreateExpression(listDecl.Type.Clone())
				{
					Initializer = arrayInitializer
				};
				CopyInstructionAnnotationsFromSources(initializerValue, matchedStatements, values);
				nextIndex = i + 1;
				return true;
			}

			if (TryCollectSpanAssignmentValue(statement, spanVarName, out var value))
			{
				values.Add(value.Clone());
				matchedStatements.Add(statement);
				continue;
			}

			if (IsAllowedPreludeNoise(statement))
			{
				matchedStatements.Add(statement);
				continue;
			}

			return false;
		}

		return false;
	}

	private static bool TryMatchObjectMemberSimpleAssignment(
		Statement statement,
		string objectVariableName,
		out string memberName,
		out Expression memberValue)
	{
		memberName = string.Empty;
		memberValue = Expression.Null;
		if (statement is not ExpressionStatement
			{
				Expression: AssignmentExpression
				{
					Operator: AssignmentOperatorType.Assign,
					Left: MemberReferenceExpression
					{
						Target: IdentifierExpression { Identifier: var targetName },
						MemberName: var assignedMemberName
					},
					Right: var right
				}
			}
			|| !string.Equals(targetName, objectVariableName, StringComparison.Ordinal))
		{
			return false;
		}

		if (!IsAllowedSimpleInitializerExpression(right))
		{
			return false;
		}

		memberName = assignedMemberName;
		memberValue = right;
		return true;
	}

	private static bool TryMatchTerminalObjectMemberAssignment(
		Statement statement,
		string listVarName,
		string objectVariableName,
		out string memberName)
	{
		memberName = string.Empty;
		if (statement is not ExpressionStatement
			{
				Expression: AssignmentExpression
				{
					Operator: AssignmentOperatorType.Assign,
					Left: MemberReferenceExpression
					{
						Target: IdentifierExpression { Identifier: var targetName },
						MemberName: var assignedMemberName
					},
					Right: IdentifierExpression { Identifier: var rightName }
				}
			}
			|| !string.Equals(targetName, objectVariableName, StringComparison.Ordinal)
			|| !string.Equals(rightName, listVarName, StringComparison.Ordinal))
		{
			return false;
		}

		memberName = assignedMemberName;
		return true;
	}

	private static bool TryMatchRefSlotAssignment(Statement statement, string slotVariableName, string objectVariableName)
	{
		if (statement is not ExpressionStatement
			{
				Expression: AssignmentExpression
				{
					Operator: AssignmentOperatorType.Assign,
					Left: IdentifierExpression { Identifier: var leftName },
					Right: IdentifierExpression { Identifier: var rightName }
				}
			})
		{
			return false;
		}

		return string.Equals(leftName, slotVariableName, StringComparison.Ordinal)
			&& string.Equals(rightName, objectVariableName, StringComparison.Ordinal);
	}

	private static void AddObjectMemberInitializer(ObjectCreateExpression objectValue, string memberName, Expression memberValue)
	{
		objectValue.Initializer ??= new ArrayInitializerExpression();
		var namedInitializer = new NamedExpression(memberName, memberValue.Clone());
		CopyInstructionAnnotationsFromSource(namedInitializer, memberValue);
		objectValue.Initializer.Elements.Add(namedInitializer);
	}

	private static bool IsAllowedPreludeNoise(Statement statement)
	{
		// Numeric temp declarations and increments are common in this lowering pattern.
		if (statement is VariableDeclarationStatement
			{
				Variables.Count: 1
			} variableDecl
			&& variableDecl.Variables.FirstOrDefault() is { Name: var varName }
			&& IsTempCounterName(varName))
		{
			return true;
		}

		if (statement is ExpressionStatement
			{
				Expression: UnaryOperatorExpression
				{
					Operator: UnaryOperatorType.PostIncrement,
					Expression: IdentifierExpression { Identifier: var incrementIdentifier }
				}
			}
			&& IsTempCounterName(incrementIdentifier))
		{
			return true;
		}

		if (statement is ExpressionStatement
			{
				Expression: AssignmentExpression
				{
					Operator: AssignmentOperatorType.Assign,
					Left: IdentifierExpression { Identifier: var assignmentIdentifier }
				}
			}
			&& IsTempCounterName(assignmentIdentifier))
		{
			return true;
		}

		return false;
	}

	private static bool IsTempCounterName(string identifier)
	{
		return identifier.StartsWith("num", StringComparison.Ordinal);
	}

	private static void CleanupLeadingTempNoise(ConstructorDeclaration constructorDeclaration)
	{
		var statements = constructorDeclaration.Body.Statements.ToArray();
		var prefixNoise = new List<Statement>();

		foreach (var statement in statements)
		{
			if (!IsAllowedPreludeNoise(statement))
			{
				break;
			}

			prefixNoise.Add(statement);
		}

		if (prefixNoise.Count == 0)
		{
			return;
		}

		var statementsAfterNoise = statements.Skip(prefixNoise.Count).ToArray();
		foreach (var statement in prefixNoise)
		{
			// Declarations are removed only when no longer referenced after the generated prelude.
			if (statement is VariableDeclarationStatement
				{
					Variables.Count: 1
				} declaration
				&& declaration.Variables.FirstOrDefault() is { Name: var name }
				&& IsTempCounterName(name))
			{
				if (!IsIdentifierReferencedOutsidePrefix(name, statementsAfterNoise, constructorDeclaration.Initializer))
				{
					statement.Remove();
				}

				continue;
			}

			statement.Remove();
		}
	}

	private static bool IsIdentifierReferencedOutsidePrefix(
		string identifier,
		IReadOnlyList<Statement> statementsAfterPrefix,
		ConstructorInitializer constructorInitializer)
	{
		foreach (var statement in statementsAfterPrefix)
		{
			if (statement.Descendants.OfType<IdentifierExpression>()
					.Any(id => string.Equals(id.Identifier, identifier, StringComparison.Ordinal)))
			{
				return true;
			}
		}

		if (!constructorInitializer.IsNull
			&& constructorInitializer.Descendants.OfType<IdentifierExpression>()
				.Any(id => string.Equals(id.Identifier, identifier, StringComparison.Ordinal)))
		{
			return true;
		}

		return false;
	}

	private static bool TryMatchTerminalAssignment(Statement statement, string listVarName, out string targetMemberName)
	{
		targetMemberName = string.Empty;
		if (statement is not ExpressionStatement { Expression: AssignmentExpression { Operator: AssignmentOperatorType.Assign } assignment })
		{
			return false;
		}

		if (!TryGetAssignedMemberName(assignment.Left, out targetMemberName))
		{
			return false;
		}

		return assignment.Right is IdentifierExpression { Identifier: var rightName }
			&& string.Equals(rightName, listVarName, StringComparison.Ordinal);
	}

	private static bool TryBuildConstructorInitializer(
		ConstructorDeclaration constructorDeclaration,
		IReadOnlyList<Statement> statements,
		int boundaryIndex,
		Statement boundaryStatement,
		Dictionary<string, Expression> matchedListInitByLocal,
		out ConstructorInitializer constructorInitializer,
		out List<Statement> matchedCtorPreludeStatements)
	{
		constructorInitializer = ConstructorInitializer.Null;
		matchedCtorPreludeStatements = [];
		if (!TryGetCtorInvocation(boundaryStatement, out var initType, out var invocation))
		{
			return false;
		}

		var newInitializer = new ConstructorInitializer
		{
			ConstructorInitializerType = initType
		};

		foreach (var arg in invocation.Arguments)
		{
			switch (arg)
			{
				case IdentifierExpression { Identifier: var localName } when matchedListInitByLocal.TryGetValue(localName, out var recoveredExpression):
					newInitializer.Arguments.Add(recoveredExpression.Clone());
					break;
				case IdentifierExpression { Identifier: var localName }:
					if (constructorDeclaration.Parameters.Any(parameter => parameter.Name == localName))
					{
						newInitializer.Arguments.Add(arg.Clone());
						break;
					}
					if (!TryMatchCtorArgumentListPrelude(
							statements,
							boundaryIndex,
							localName,
							out var ctorArgInitializer,
							out var matchedStatements))
					{
						return false;
					}
					newInitializer.Arguments.Add(ctorArgInitializer);
					matchedCtorPreludeStatements.AddRange(matchedStatements);
					break;
				case PrimitiveExpression:
				case NullReferenceExpression:
				case ObjectCreateExpression:
					newInitializer.Arguments.Add(arg.Clone());
					break;
				default:
					return false;
			}
		}

		constructorInitializer = newInitializer;
		return true;
	}

	private static bool TryMatchCtorArgumentListPrelude(
		IReadOnlyList<Statement> statements,
		int boundaryIndex,
		string listVarName,
		out Expression initializer,
		out List<Statement> matchedStatements)
	{
		initializer = Expression.Null;
		matchedStatements = [];
		int startIndex = -1;
		VariableDeclarationStatement? listDecl = null;
		for (int i = 0; i < boundaryIndex; i++)
		{
			if (statements[i] is VariableDeclarationStatement variableDeclaration
				&& variableDeclaration.Variables.Count == 1
				&& variableDeclaration.Variables.FirstOrDefault() is { } variable
				&& string.Equals(variable.Name, listVarName, StringComparison.Ordinal)
				&& variable.Initializer is ObjectCreateExpression)
			{
				startIndex = i;
				listDecl = variableDeclaration;
			}
		}

		if (startIndex < 0 || listDecl == null || startIndex + 2 >= boundaryIndex)
		{
			return false;
		}

		if (!IsCollectionsMarshalCall(statements[startIndex + 1], "SetCount", listVarName))
		{
			return false;
		}

		if (statements[startIndex + 2] is not VariableDeclarationStatement spanDecl
			|| spanDecl.Variables.Count != 1
			|| spanDecl.Variables.FirstOrDefault() is not { } spanVariable
			|| spanVariable.Initializer is not InvocationExpression spanInit
			|| !IsCollectionsMarshalAsSpanCall(spanInit, listVarName))
		{
			return false;
		}

		string spanVarName = spanVariable.Name;
		var values = new List<Expression>();
		for (int i = startIndex + 3; i < boundaryIndex; i++)
		{
			var statement = statements[i];
			if (TryCollectSpanAssignmentValue(statement, spanVarName, out var value))
			{
				values.Add(value.Clone());
				continue;
			}

			if (!IsAllowedPreludeNoise(statement))
			{
				return false;
			}
		}

		if (values.Count == 0)
		{
			return false;
		}

		for (int i = startIndex; i < boundaryIndex; i++)
		{
			matchedStatements.Add(statements[i]);
		}
		initializer = new ObjectCreateExpression(listDecl.Type.Clone())
		{
			Initializer = CopyInstructionAnnotationsFromSources(
				new ArrayInitializerExpression(values),
				matchedStatements,
				values)
		};
		CopyInstructionAnnotationsFromSources(initializer, matchedStatements, values);
		return true;
	}

	private static void ReorderRecoveredMembers(
		TypeDeclaration typeDeclaration,
		List<string> recoveredMemberOrder,
		Dictionary<string, EntityDeclaration> memberMap)
	{
		var uniqueNames = recoveredMemberOrder.Distinct(StringComparer.Ordinal).ToList();
		if (uniqueNames.Count < 2)
		{
			return;
		}

		var participatingMembers = uniqueNames
			.Where(memberMap.ContainsKey)
			.Select(name => memberMap[name])
			.Distinct()
			.ToList();
		if (participatingMembers.Count < 2)
		{
			return;
		}

		var currentMembers = typeDeclaration.Members.ToArray();
		var participatingIndices = currentMembers
			.Select((member, index) => (member, index))
			.Where(tuple => participatingMembers.Contains(tuple.member))
			.Select(tuple => tuple.index)
			.ToArray();
		if (participatingIndices.Length < 2)
		{
			return;
		}
		var participatingSet = participatingMembers.ToHashSet();
		var reorderedParticipants = uniqueNames
			.Select(name => memberMap.TryGetValue(name, out var member) ? member : null)
			.Where(member => member != null && participatingSet.Contains(member))
			.Distinct()
			.Cast<EntityDeclaration>()
			.ToList();
		if (reorderedParticipants.Count != participatingMembers.Count)
		{
			// Ambiguous mapping; keep declaration order unchanged.
			return;
		}

		var rewrittenMembers = currentMembers.ToArray();
		int nextParticipant = 0;
		for (int i = 0; i < currentMembers.Length; i++)
		{
			if (participatingSet.Contains(currentMembers[i]))
			{
				rewrittenMembers[i] = reorderedParticipants[nextParticipant++];
			}
		}

		foreach (var member in currentMembers)
		{
			member.Remove();
		}

		foreach (var member in rewrittenMembers)
		{
			typeDeclaration.InsertChildBefore(typeDeclaration.RBraceToken, member, Roles.TypeMemberRole);
		}
	}

	private readonly record struct ListPreludeMatch(
		string ListVariableName,
		string TargetMemberName,
		Expression InitializerExpression,
		List<Statement> MatchedStatements,
		int NextIndex
	);

	private readonly record struct ConditionalTempAssignmentMatch(
		string TargetMemberName,
		Expression InitializerExpression,
		List<Statement> MatchedStatements,
		int NextIndex
	);

	private readonly record struct SpreadSegment(bool IsSpread, Expression Expression)
	{
		public static SpreadSegment FromRange(Expression expression)
		{
			return new SpreadSegment(true, expression);
		}

		public static SpreadSegment FromValue(Expression expression)
		{
			return new SpreadSegment(false, expression);
		}
	}
}
