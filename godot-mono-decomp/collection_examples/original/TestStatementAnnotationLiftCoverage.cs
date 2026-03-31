using System.Collections.Generic;

namespace ConsoleApp2;

public static class TestStatementAnnotationLiftCoverage
{
	public class Nested
	{
		public List<int> Numbers { get; set; } = [];

		public string Name { get; set; } = string.Empty;
	}

	public class CoverageClass
	{
		public List<int> Values { get; set; } = [1, 2, 3];

		public Nested nested = new Nested
		{
			Numbers = [4, 5, 6],
			Name = "nested"
		};

		public readonly List<int> CtorArgs;

		public CoverageClass()
			: this([7, 8, 9])
		{
		}

		public CoverageClass(List<int> ctorArgs)
		{
			CtorArgs = ctorArgs;
		}
	}
}
