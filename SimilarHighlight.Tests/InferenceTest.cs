﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Code2Xml.Core;
using Code2Xml.Core.Location;
using NUnit.Framework;

namespace SimilarHighlight.Tests
{
	[TestFixture]
    public class InferenceTest
    {
        public void Inference() {
            int local_int_A = 123;
            string local_string_B = "hello";
            string[] strNum = new string[] { 
                "one", "two", "three","four", "five", "six", "seven", "eight", "nine",
            };
        }

		[Test]
        [TestCase(@"../../../SimilarHighlight.Tests/InferenceTest.cs")]
		public void TestGetSimilarElements(string path) {
			var processor = new Code2Xml.Languages.ANTLRv3.Generators.CSharp.CSharpCstGeneratorUsingAntlr3();	// processorIdentifier indicates here
			var fileInfo = new FileInfo(path);					// fileInfoIdentifier indicates here
			var code = File.ReadAllText(path);
            var xml = processor.GenerateTreeFromCodeText(code, true);
			var elements = xml.Descendants("identifier").ToList();

			// Create locatoin information that user selects in the editor
			//
			// This test creates location information by analyzing ASTs
			// Actually, in real usage, you should create CodeRange instance
			// from locations that user selected
			//
			// You can create CodeRange instance from the location information,
			// that is, source code, a start index, and an end index
			// using CodeRange.ConvertFromIndicies()
			//

            var firstRange = CodeRange.Locate(elements.First(e => e.TokenText == "processor"));
            var secondRange = CodeRange.Locate(elements.First(e => e.TokenText == "fileInfo"));
			var processorIdentifier = new LocationInfo {
                CodeRange = firstRange,
                CstNode = firstRange.FindOutermostElement(xml),
			};
			var fileInfoIdentifier = new LocationInfo {
                CodeRange = secondRange,
                CstNode = secondRange.FindOutermostElement(xml),
			};

			// Get similar nodes
            //var ret = Inferrer.GetSimilarElements(processor, new[] { processorIdentifier, fileInfoIdentifier },
            //        xml);

            //// Show the similar nodes
            //foreach (var tuple in ret.Take(10)) {
            //    var score = tuple.Item1;
            //    var codeRange = tuple.Item2;
            //    var startAndEnd = codeRange.ConvertToIndicies(code);
            //    var fragment = code.Substring(startAndEnd.Item1, startAndEnd.Item2 - startAndEnd.Item1);
            //    Console.WriteLine("Similarity: " + score + ", code: " + fragment);
            //}           
		}
    }
}
