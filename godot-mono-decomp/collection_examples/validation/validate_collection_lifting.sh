#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
REPO_DIR="$(cd "$SCRIPT_DIR/../.." && pwd)"
VALIDATION_PROJECT="$SCRIPT_DIR/CollectionExamplesValidation.csproj"
ASSEMBLY_PATH="$SCRIPT_DIR/bin/Debug/net10.0/CollectionExamplesValidation.dll"
CLI_PROJECT="$REPO_DIR/GodotMonoDecompCLI/GodotMonoDecompCLI.csproj"
ORIGINAL_PROJECT_DIR="$REPO_DIR/collection_examples/original"
BASELINE_CTOR_FILE="$REPO_DIR/collection_examples/decompiled/TestCtorBoundaryCoverage.cs"
UPDATE_FIXTURES=true

for arg in "$@"; do
	case "$arg" in
		--update-fixtures)
			UPDATE_FIXTURES=true
			;;
		--no-update-fixtures)
			UPDATE_FIXTURES=false
			;;
		-h|--help)
			echo "Usage: $0 [--update-fixtures|--no-update-fixtures]"
			echo "  --update-fixtures     Write output to validation/decompiled_out (default)"
			echo "  --no-update-fixtures  Write output to a temporary directory"
			exit 0
			;;
		*)
			echo "Unknown argument: $arg"
			echo "Use --help for usage."
			exit 2
			;;
	esac
done

if $UPDATE_FIXTURES; then
	OUTPUT_DIR="$SCRIPT_DIR/decompiled_out"
	echo "Using tracked output directory: $OUTPUT_DIR"
else
	OUTPUT_DIR="$(mktemp -d "${TMPDIR:-/tmp}/collection-lifting-XXXXXX")"
	echo "Using temporary output directory: $OUTPUT_DIR"
	trap 'rm -rf "$OUTPUT_DIR"' EXIT
fi

echo "[1/7] Baseline ILSpy sanity checks"
test -f "$BASELINE_CTOR_FILE"
grep -q "base._002Ector(" "$BASELINE_CTOR_FILE"
grep -q "CollectionsMarshal.SetCount" "$BASELINE_CTOR_FILE"
grep -q "data = ComputeData();" "$BASELINE_CTOR_FILE"

echo "[2/7] Building validation input assembly"
dotnet build "$VALIDATION_PROJECT"

echo "[3/7] Running decompiler CLI"
echo "Output directory: $OUTPUT_DIR"
mkdir -p "$OUTPUT_DIR"
if $UPDATE_FIXTURES; then
	rm -f "$OUTPUT_DIR/TestCollectionExpressionInitializers.cs" \
		"$OUTPUT_DIR/TestCollectionInitWithSpread.cs" \
		"$OUTPUT_DIR/TestFuncInitializer.cs" \
		"$OUTPUT_DIR/TestCtorBoundaryCoverage.cs" \
		"$OUTPUT_DIR/TestInterleavedStaticCollectionInit.cs" \
		"$OUTPUT_DIR/TestStatementAnnotationLiftCoverage.cs" \
		"$OUTPUT_DIR/TestNestedCollectionExpressionInitializers.cs" \
		"$OUTPUT_DIR/CollectionExamplesValidation.Decompiled.csproj" \
		"$OUTPUT_DIR/CollectionExamplesValidation.Decompiled.sln"
fi

dotnet run --project "$CLI_PROJECT" -- "$ASSEMBLY_PATH" \
	--output-dir "$OUTPUT_DIR" \
	--project-name "CollectionExamplesValidation.Decompiled" \
	--extracted-project "$ORIGINAL_PROJECT_DIR" \
	--enable-collection-initializer-lifting

echo "[4/7] Asserting expected output files exist"
test -f "$OUTPUT_DIR/TestCollectionExpressionInitializers.cs"
test -f "$OUTPUT_DIR/TestCollectionInitWithSpread.cs"
test -f "$OUTPUT_DIR/TestFuncInitializer.cs"
test -f "$OUTPUT_DIR/TestNestedCollectionExpressionInitializers.cs"
test -f "$OUTPUT_DIR/TestCtorBoundaryCoverage.cs"
test -f "$OUTPUT_DIR/TestInterleavedStaticCollectionInit.cs"
test -f "$OUTPUT_DIR/TestStatementAnnotationLiftCoverage.cs"
test -f "$OUTPUT_DIR/CollectionExamplesValidation.Decompiled.csproj"

echo "[5/7] Asserting expected lifted and preserved markers"
grep -q "public readonly List<string> strings = new List<string>" "$OUTPUT_DIR/TestCollectionExpressionInitializers.cs"
grep -q "public static readonly List<ElemClass> elems = new List<ElemClass>" "$OUTPUT_DIR/TestCollectionExpressionInitializers.cs"
grep -q "public List<int> ListProp1 { get; set; } = new List<int>" "$OUTPUT_DIR/TestCollectionExpressionInitializers.cs"
grep -q "public readonly HashSet<int> set = new HashSet<int> { 1, 2, 3, 4, 5 };" "$OUTPUT_DIR/TestCollectionExpressionInitializers.cs"
grep -q "foo();" "$OUTPUT_DIR/TestCollectionExpressionInitializers.cs"
grep -q "public TailBoundaryDerived()" "$OUTPUT_DIR/TestCtorBoundaryCoverage.cs"
grep -q ": base(new List<int> { 1, 2, 3 })" "$OUTPUT_DIR/TestCtorBoundaryCoverage.cs"
grep -q "public TransitionBoundaryDerived()" "$OUTPUT_DIR/TestCtorBoundaryCoverage.cs"
grep -q "data = ComputeData();" "$OUTPUT_DIR/TestCtorBoundaryCoverage.cs"
grep -q "public List<ElemClassWithCollection> elems = new List<ElemClassWithCollection>" "$OUTPUT_DIR/TestNestedCollectionExpressionInitializers.cs"
grep -q "intListField = new List<int>" "$OUTPUT_DIR/TestNestedCollectionExpressionInitializers.cs"
grep -q "public static readonly List<ElemClass> staticElemClassListField = new List<ElemClass>" "$OUTPUT_DIR/TestInterleavedStaticCollectionInit.cs"
grep -q "public static List<int> StaticIntProp1 { get; } = new List<int>" "$OUTPUT_DIR/TestInterleavedStaticCollectionInit.cs"
grep -q "public static readonly List<string> staticStringListField = new List<string>" "$OUTPUT_DIR/TestInterleavedStaticCollectionInit.cs"
grep -q "public readonly List<string> strings = new List<string>" "$OUTPUT_DIR/TestInterleavedStaticCollectionInit.cs"
grep -q "public List<int> ListProp1 { get; set; } = new List<int>" "$OUTPUT_DIR/TestInterleavedStaticCollectionInit.cs"
grep -q "private Func<string, bool> filter = (string s) => s.Length > 3;" "$OUTPUT_DIR/TestFuncInitializer.cs"
grep -q "= new List<int> { 1, 2, 3 };" "$OUTPUT_DIR/TestStatementAnnotationLiftCoverage.cs"
grep -q ": this(new List<int> { 7, 8, 9 })" "$OUTPUT_DIR/TestStatementAnnotationLiftCoverage.cs"
grep -Fq "public static readonly HashSet<string> strings = new HashSet<string>([..stringListConst1" "$OUTPUT_DIR/TestCollectionInitWithSpread.cs"
grep -Fq "public static readonly List<string> strings = new List<string>([..stringListConst1" "$OUTPUT_DIR/TestCollectionInitWithSpread.cs"
grep -Fq "public static readonly IReadOnlySet<string> strings = new HashSet<string>([..stringListConst1" "$OUTPUT_DIR/TestCollectionInitWithSpread.cs"
grep -Fq "public static readonly IReadOnlyList<string> strings = [..stringListConst1" "$OUTPUT_DIR/TestCollectionInitWithSpread.cs"
line_intprop1="$(grep -n "public int IntProp1 { get; set; } = 1;" "$OUTPUT_DIR/TestCollectionExpressionInitializers.cs" | awk -F: 'NR==1 {print $1}')"
line_listprop1="$(grep -n "public List<int> ListProp1 { get; set; } = new List<int>" "$OUTPUT_DIR/TestCollectionExpressionInitializers.cs" | awk -F: 'NR==1 {print $1}')"
line_intprop2="$(grep -n "public int IntProp2 { get; set; } = 2;" "$OUTPUT_DIR/TestCollectionExpressionInitializers.cs" | awk -F: 'NR==1 {print $1}')"
line_set="$(grep -n "public readonly HashSet<int> set = new HashSet<int> { 1, 2, 3, 4, 5 };" "$OUTPUT_DIR/TestCollectionExpressionInitializers.cs" | awk -F: 'NR==1 {print $1}')"
if [ -z "$line_intprop1" ] || [ -z "$line_listprop1" ] || [ -z "$line_intprop2" ] || [ -z "$line_set" ] \
	|| [ "$line_intprop1" -ge "$line_listprop1" ] || [ "$line_listprop1" -ge "$line_intprop2" ] || [ "$line_intprop2" -ge "$line_set" ]; then
	echo "Unexpected TestClass1 declaration order: expected IntProp1 -> ListProp1 -> IntProp2 -> set."
	exit 1
fi
if grep -q "ref ElemClassWithCollection reference" "$OUTPUT_DIR/TestNestedCollectionExpressionInitializers.cs"; then
	echo "Found unexpected imperative nested ref-slot assignments in decompiled nested output."
	exit 1
fi
if grep -q "AddRange(" "$OUTPUT_DIR/TestCollectionInitWithSpread.cs" \
	|| grep -q "foreach (string item" "$OUTPUT_DIR/TestCollectionInitWithSpread.cs"; then
	echo "Found unexpected spread prelude statements in decompiled spread output."
	exit 1
fi
if grep -q "new _003C_003Ez__ReadOnlyList<string>(new List<string>(\\[\\." "$OUTPUT_DIR/TestCollectionInitWithSpread.cs"; then
	echo "Found unexpected redundant ReadOnlyList(List(collection-expression)) wrapper."
	exit 1
fi

echo "[6/7] Asserting removed ctor-artifact markers"
if grep -q "_002Ector(" "$OUTPUT_DIR/TestCollectionExpressionInitializers.cs" \
	|| grep -q "_002Ector(" "$OUTPUT_DIR/TestCollectionInitWithSpread.cs" \
	|| grep -q "_002Ector(" "$OUTPUT_DIR/TestFuncInitializer.cs" \
	|| grep -q "_002Ector(" "$OUTPUT_DIR/TestNestedCollectionExpressionInitializers.cs" \
	|| grep -q "_002Ector(" "$OUTPUT_DIR/TestCtorBoundaryCoverage.cs" \
	|| grep -q "_002Ector(" "$OUTPUT_DIR/TestInterleavedStaticCollectionInit.cs" \
	|| grep -q "_002Ector(" "$OUTPUT_DIR/TestStatementAnnotationLiftCoverage.cs"; then
	echo "Found unexpected _002Ector(...) artifact in decompiled output."
	exit 1
fi

echo "[7/7] Building decompiled project"
dotnet build "$OUTPUT_DIR/CollectionExamplesValidation.Decompiled.csproj"

echo "Validation passed."
