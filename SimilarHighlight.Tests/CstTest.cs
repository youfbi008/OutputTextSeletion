﻿using System;
using System.Diagnostics;
using System.Collections.Generic;
using Code2Xml.Core.Generators;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Code2Xml.Core;
using Code2Xml.Core.Location;
using Paraiba.Collections.Generic;
using Paraiba.Linq;
using System.Threading.Tasks;
using System.Threading;

namespace SimilarHighlight
{
    public static class CstInferrer
    {
        private static int keysCount { get; set; }

        // TODO: When the elements from different method, the different will be considered.
        public static HashSet<string> GetSurroundingKeys(
                this CstNode node, int length, bool inner = true, bool outer = true)
        {
            //inner = outer = true; // TODO: for debug

            var ret = new HashSet<string>();
            var childElements = new List<Tuple<CstNode, string>>();
            if (inner)
            {
                childElements.Add(Tuple.Create(node, node.Name));
                var ancestorStr = "";
                foreach (var e in node.AncestorsWithSingleChildAndSelf())
                {
                    // null?
                    if (e == null) {
                        continue;
                    }
                    ancestorStr = ancestorStr + "<" + e.NameWithId();
                    ret.Add(ancestorStr);
                }
            }
            var i = 1;
            if (outer)
            {
                var parentElement = Tuple.Create(node, node.Name);
                var descendantStr = "";
                foreach (var e in node.DescendantsOfSingleAndSelf())
                {
                    descendantStr = descendantStr + "<" + e.NameWithId();
                    ret.Add(descendantStr);
                }
                ret.Add(node.NameOrTokenWithId());
                for (; i <= length; i++)
                {
                    var newChildElements = new List<Tuple<CstNode, string>>();
                    foreach (var t in childElements)
                    {
                        foreach (var e in t.Item1.Elements())
                        {
                            var key = t.Item2 + ">" + e.NameOrTokenWithId();
                            newChildElements.Add(Tuple.Create(e, key));
                            ret.Add(t.Item2 + ">'" + e.TokenText + "'");
                        }
                    }
                    foreach (var e in parentElement.Item1.Siblings(10))
                    {
                        var key = parentElement.Item2 + "-" + e.NameOrTokenWithId();
                        newChildElements.Add(Tuple.Create(e, key));
                        ret.Add(parentElement.Item2 + "-'" + e.TokenText + "'");
                    }
                    ret.UnionWith(newChildElements.Select(t => t.Item2));
                    childElements = newChildElements;

                    var newParentElement = parentElement.Item1.Parent;
                    if (newParentElement == null)
                    {
                        break;
                    }
                    parentElement = Tuple.Create(
                            newParentElement,
                            parentElement.Item2 + "<" + newParentElement.NameOrTokenWithId());
                    ret.Add(parentElement.Item2);
                }
            }
            for (; i <= length; i++)
            {
                var newChildElements = new List<Tuple<CstNode, string>>();
                if (childElements == null)
                {
                    break;
                }
                foreach (var t in childElements)
                {
                    foreach (var e in t.Item1.Elements())
                    {
                        var key = t.Item2 + ">" + e.NameOrTokenWithId();
                        newChildElements.Add(Tuple.Create(e, key));
                        ret.Add(t.Item2 + ">'" + e.TokenText + "'");
                    }
                }
                ret.UnionWith(newChildElements.Select(t => t.Item2));
                childElements = newChildElements;
            }
            return ret;
        }

        public static HashSet<string> GetCommonKeys(
                this IEnumerable<CstNode> elements, int length, bool inner = true, bool outer = true)
        {
            HashSet<string> commonKeys = null;
      //      keysCount = 0;
            
            foreach (var element in elements) {
                // Get the data collection of the surrounding nodes.
                var keys = element.GetSurroundingKeys(length, inner, outer);
                //int i = 0;
                //foreach (var k in keys) {
                //    Debug.WriteLine("[" + i + "]:" + k); i++;
                //}

                // Accumulate the number of the surrounding nodes.
            //    keysCount += keys.Count();
                if (commonKeys == null)
                {
                    commonKeys = keys;
                }
                else
                {
                    commonKeys.IntersectWith(keys);
                }
            }
            return commonKeys;
        }

        private static ISet<string> AdoptNodeNames(ICollection<CstNode> outermosts)
        {
            var name2Count = new Dictionary<string, int>();
            var candidates = outermosts.AsParallel().SelectMany(
                    e => e.DescendantsOfSingleAndSelf());
            foreach (var e in candidates)
            {
                var count = name2Count.GetValueOrDefault(e.Name);
                name2Count[e.Name] = count + 1;
            }
            return outermosts.AsParallel().Select(
                    e => e.DescendantsOfSingleAndSelf()
                            .Select(e2 => e2.Name)
                            .MaxElementOrDefault(name => name2Count[name]))
                    .ToHashSet();
        }

        public static IEnumerable<Tuple<int, CodeRange>> GetSimilarElements(
                IEnumerable<LocationInfo> locations, CstNode rootNode, ref ISet<string> nodeNames,
                int range = 5, bool inner = true, bool outer = true)
        {
            try
            {
                // Convert the location informatoin (CodeRange) to the node (XElement) in the CSTs
                var elements = new List<CstNode>();

                foreach (var location in locations)
                {
                    elements.Add(location.CstNode);
                }

                // Determine the node names to extract candidate nodes from the CSTs
                nodeNames = AdoptNodeNames(elements);

                var names = nodeNames;
                // Extract candidate nodes that has one of the determined names
                var candidates = new List<IEnumerable<CstNode>>();

                TimeWatch.Start();

                candidates.Add(
                        rootNode.Descendants().AsParallel()
                                .Where(e => names.Contains(e.Name)).ToList());

                // Extract common surrounding nodes from the selected elements.
                var commonKeys = elements.GetCommonKeys(range, true, true);
                //int i = 0;
                //foreach (var k in commonKeys)
                //{
                //    Debug.WriteLine("[" + i + "]:" + k); i++;
                //}
                TimeWatch.Stop("FindOutCandidateElements");

                var minSimilarity = GetMinSimilarity((double)commonKeys.Count, names);

                TimeWatch.Start();

                // Get the similar nodes collection. 
                var ret = candidates.GetSimilars(commonKeys, minSimilarity);

                if (names.Contains("element_initializer")) {
                    var retArray = candidates.GetOtherSimilars(commonKeys);
                    var retOthers = new List<Tuple<int, CodeRange>>();
                    // To extract the current Array by comparaison siblings of the first node in each array with selected elements.
                    foreach (var e in retArray)
                    {
                        var cnt = e.Item2.Siblings().Count(elements.Contains);
                        if (cnt >= 2)
                        {
                            retOthers.Add(Tuple.Create(
                                                    e.Item1,	// Indicates the simlarity
                                                    CodeRange.Locate(e.Item2)
                                                    )
                                         );
                            break;
                        }
                        else if (cnt >= 1)
                        {
                            retOthers.Add(Tuple.Create(
                                                    e.Item1,	// Indicates the simlarity
                                                    CodeRange.Locate(e.Item2)
                                                    )
                                         );
                            continue;
                        }
                    }

                    if (retOthers.Count > 0)
                    {
                        ret = ret.Concat(retOthers).ToList();
                    }
                }

                TimeWatch.Stop("FindOutSimilarElements");
                return ret;
            }
            catch (ThreadAbortException tae)
            {
                HLTextTagger.OutputMsgForExc("Background thread of highlighting is stopping.[CstInferrer.GetSimilarElements method]");
            }
            catch (Exception exc)
            {
                HLTextTagger.OutputMsgForExc(exc.ToString());
            }
            return null;
        }

        private static IEnumerable<Tuple<int, CodeRange>> GetSimilars(this List<IEnumerable<CstNode>> candidates,
            HashSet<string> commonKeys, double minSimilarity, int range = 5, bool inner = true, bool outer = true)
        {
            return candidates.AsParallel().SelectMany(
                        kv =>
                        {
                            return kv.Select(
                                    e => Tuple.Create(
                                        // Count how many common surrounding nodes each candidate node has 
                                        e.GetSurroundingKeys(range, inner, outer)
                                            .Count(commonKeys.Contains),
                                            e))
                                // The candidate node will be taken as similar node 
                                // when the number of common surrounding nodes is bigger than the similarity threshold.
                                     .Where(e => e.Item1 >= minSimilarity
                                     )
                                     .Select(
                                            t => Tuple.Create(
                                                    t.Item1,	// Indicates the simlarity
                                                    CodeRange.Locate(t.Item2)
                                                    ));
                        })
                        // Sort candidate nodes using the similarities
                        //.OrderByDescending(t => t.Item1)
                        .ToList();//.ToList()
        }

        private static IEnumerable<Tuple<int, CstNode>> GetOtherSimilars(this List<IEnumerable<CstNode>> candidates,
            HashSet<string> commonKeys, int range = 5, bool inner = true, bool outer = true)
        {
            return candidates.AsParallel().SelectMany(
                        kv =>
                        {
                            return kv.Where(e => e.RuleId == "334")
                                     .Select(
                                        e => Tuple.Create(
                                            // Count how many common surrounding nodes each candidate node has 
                                            e.GetSurroundingKeys(range, inner, outer)
                                                .Count(commonKeys.Contains),
                                                e));
                        })
                // Sort candidate nodes using the similarities
                //.OrderByDescending(t => t.Item1)
                        .ToList();//
        }

        private static double GetMinSimilarity(double commonCount, ISet<string> names)
        {
            double minSimilarity = 0;
            if (commonCount <= 5)
            {
                if (HLTextTagger.OptionPage.SimilarityLevel == Option.OptionPage.SimilarityType.High)
                {
                    minSimilarity = commonCount;
                }
                else if (HLTextTagger.OptionPage.SimilarityLevel == Option.OptionPage.SimilarityType.Stardard)
                {
                    minSimilarity = commonCount - 1;
                }
                else if (HLTextTagger.OptionPage.SimilarityLevel == Option.OptionPage.SimilarityType.Low)
                {
                    minSimilarity = commonCount - 2;
                }
            }
            else {
                if (HLTextTagger.OptionPage.SimilarityLevel == Option.OptionPage.SimilarityType.High)
                {
                    if (commonCount <= 10)
                    {
                        minSimilarity = commonCount - 1;
                    }
                    else {
                        if (names.Contains("element_initializer"))
                        {
                            minSimilarity = commonCount * (double)Option.OptionPage.SimilarityType.Low / 10;
                        }
                        else
                        {
                            minSimilarity = commonCount * (double)Option.OptionPage.SimilarityType.High / 10;
                        }
                    }
                }
                else if (HLTextTagger.OptionPage.SimilarityLevel == Option.OptionPage.SimilarityType.Stardard)
                {
                    minSimilarity = commonCount * (double)Option.OptionPage.SimilarityType.Stardard / 10;
                }
                else if (HLTextTagger.OptionPage.SimilarityLevel == Option.OptionPage.SimilarityType.Low)
                {
                    minSimilarity = commonCount * (double)Option.OptionPage.SimilarityType.Low / 10;
                }
            }
            if (minSimilarity < 1) {
                minSimilarity = 1;
            }
            return minSimilarity;
        }

        void function_A13()
        {
            int local_int_A = 321321;
            string local_string_B = "local string B";
            this.global_int_A = local_int_A;
            this.global_string_B = local_string_B;
            this.strNum = new string[] { 
                "one", "two", "three", "four", "five", "six", "seven", "eight", "nine",
            };
        }

        void function_B13(int a, string num)
        {
            int local_int_C = DateTime.Now.Year;
            string local_String_D = DateTime.Now.ToLongTimeString();
            local_String_D = this.global_string_B.ToString();

        }

        void function_C13()
        {
            switch (global_int_C)
            {
                case 111:
                    this.function_B(global_int_C, strNum[0]);
                    Console.WriteLine("one 1");
                    break;
                case 222:
                    this.function_B(global_int_C, strNum[1]);
                    Console.WriteLine("two 2");
                    break;
                case 333:
                    this.function_B(global_int_C, strNum[2]);
                    Console.WriteLine("three 3");
                    break;
            }
        }
    }
}