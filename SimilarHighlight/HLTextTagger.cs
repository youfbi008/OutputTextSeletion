﻿using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using EnvDTE;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Classification;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Operations;
using Microsoft.VisualStudio.Text.Tagging;
using Microsoft.VisualStudio.Utilities;
using Code2Xml.Core;
using Code2Xml.Core.Location;
using System.IO;
using System.Diagnostics;
using System.Xml.Linq;
using System.Xml;
using Code2Xml.Languages.ANTLRv3.Processors.CSharp;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Code2Xml.Languages.ANTLRv3.Processors.Java;
using System.Collections;


namespace SimilarHighlight
{
    internal class HLTextTagger : ITagger<HLTextTag>
    {
        IWpfTextView View { get; set; }
        ITextBuffer SourceBuffer { get; set; }
        ITextSearchService TextSearchService { get; set; }
        ITextStructureNavigator TextStructureNavigator { get; set; }
        NormalizedSnapshotSpanCollection WordSpans { get; set; }
        SnapshotSpan? CurrentWord { get; set; }
        EnvDTE.Document Document { get; set; }
        private object updateLock = new object();
        private object buildLock = new object();

        // Make the highlight operation to background.
        System.Threading.Thread listenThread;
        // The highlighted elements will be saved when the text is changed.
        NormalizedSnapshotSpanCollection TmpWordSpans { get; set; }
        // location datas 
        List<LocationInfo> Locations { get; set; }
        // the collecton of highlighted elements
        List<SnapshotSpan> NewSpanAll { get; set; }
        // Count the number of left mouse button clicks.
        int CntLeftClick { get; set; }
        // current selection
        TextSelection RequestSelection { get; set; }
        // current word for Repeat check   
        SnapshotSpan CurrentWordForCheck { get; set; }
        // the order number of current selection in highlighted elements
        int CurrentSelectNum { get; set; }
        // the temp order number of current selection in highlighted elements
        int TMPCurrentSelectNum { get; set; }
        // the processor of the current editor
        Processor Processor;
        // the source code of the current editor
        string SourceCode { get; set; }
        // the root element of the source code
        XElement RootElement { get; set; }
        // the token list of the source code
   //     List<XElement> TokenElements { get; set; }
        // Whether the shift key is pressed.
        bool IsShiftDown = false;
        // Whether the similar elements are needed to fix.
        bool IsNeedFix = false;
        // the regex wheather need fix
        Regex RegexNeedFix { get; set; }
        // the forward offset when fixing
        int startOffset { get; set; }
        // the backward offset when fixing
        int endOffset { get; set; }
        Tuple<Regex, Tuple<int, int>> FixKit = null;
        // LocationInfo of the element selected before current two element
        LocationInfo PreLocationInfo { get; set; }
        // Whether have the similar elements.
        bool HaveSimilarElements = false;

        public HLTextTagger(IWpfTextView view, ITextBuffer sourceBuffer, ITextSearchService textSearchService,
ITextStructureNavigator textStructureNavigator, EnvDTE.Document document)
        {
            if (document == null)
                return;
           
            if (this.Processor == null)
            {
                switch (Path.GetExtension(document.FullName).ToUpper()) {
                    case ".JAVA":
                        this.Processor = new Code2Xml.Languages.ANTLRv3.Processors.Java.JavaProcessorUsingAntlr3();
                        break;
                    //case ".CBL":
                    //    this.Processor = new Code2Xml.Languages.ExternalProcessors.Processors.Cobol.Cobol85Processor();
                    //    break;
                    case ".CS":
                        this.Processor = new Code2Xml.Languages.ANTLRv3.Processors.CSharp.CSharpProcessorUsingAntlr3();
                        break;
                }

                if (Processor != null)
                {

                    var currentTextDoc = document.Object("TextDocument");
                    SourceCode = currentTextDoc.StartPoint.CreateEditPoint().GetText(currentTextDoc.EndPoint);

                    this.Document = document;

                    RootElement = Processor.GenerateXml(SourceCode, true);
                    RegexNeedFix = new Regex("\"(.*)\"");
                    // the forward offset when fixing
                    startOffset = 1;
                    // the backward offset when fixing
                    endOffset = 1;

                    Locations = new List<LocationInfo>();
                    this.CntLeftClick = 0;
                    this.View = view;
                    this.View.VisualElement.PreviewMouseLeftButtonUp += VisualElement_PreviewMouseLeftButtonUp;
                    this.View.VisualElement.PreviewMouseDown += VisualElement_PreviewMouseDown;
                    this.View.VisualElement.PreviewKeyDown += VisualElement_PreviewKeyDown;
                    this.View.VisualElement.PreviewKeyUp += VisualElement_PreviewKeyUp;
                    this.SourceBuffer = sourceBuffer;
                    this.TextSearchService = textSearchService;
                    this.TextStructureNavigator = textStructureNavigator;
                    this.WordSpans = new NormalizedSnapshotSpanCollection();
                    this.TmpWordSpans = new NormalizedSnapshotSpanCollection();
                    this.CurrentWord = null;

                    //        TokenElements = RootElement.Descendants("TOKEN").ToList();
                }
            }
        }

        void VisualElement_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            // When the shift key is pressed, maybe the select operation is going to happens.
            if (Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift)
                || e.Key == Key.LeftShift || e.Key == Key.RightShift)
            {
                IsShiftDown = true;
                GetRootElement();
            }
        }

        void VisualElement_PreviewKeyUp(object sender, KeyEventArgs e)
        {
            if (Keyboard.IsKeyDown(Key.LeftCtrl) && Keyboard.IsKeyDown(Key.LeftAlt))
            {
                if (TmpWordSpans.Count() == 0)
                {
                    return;
                }
                if (e.Key == Key.Left)
                {
                    // Make the previous similar element highlighted.
                    MoveSelection("PREV");
                }
                else if (e.Key == Key.Right)
                {
                    // Make the next similar element highlighted.
                    MoveSelection("NEXT");
                }
            }
            else if (Keyboard.IsKeyDown(Key.Escape) || e.Key == Key.Escape)
            {
                // When the ESC key is pressed, the highlighting will be canceled.
                if (WordSpans.Count() != 0)
                {
                    WordSpans = new NormalizedSnapshotSpanCollection();
                    TmpWordSpans = new NormalizedSnapshotSpanCollection();
                    CurrentSelectNum = 0;
                    var tempEvent = TagsChanged;
                    if (tempEvent != null)
                        // Refresh the text of the current editor window.
                        tempEvent(this, new SnapshotSpanEventArgs(new SnapshotSpan(SourceBuffer.CurrentSnapshot, 0, SourceBuffer.CurrentSnapshot.Length)));
                }
            }
            else if (IsShiftDown && !Keyboard.IsKeyDown(Key.LeftShift) && !Keyboard.IsKeyDown(Key.RightShift))
            {
                // When the select opeartion by keyboard is over.
                RequestSelection = this.Document.Selection;
                var tmpTxt = RequestSelection.Text.Trim();
                if (tmpTxt != "")
                {
                    // Highlight by background thread.
                    ThreadStartHighlighting();
                }
            }
        }

        void VisualElement_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            // TODO 1, Fix the bug of selection is unexpectedly ignored.
            // TODO 2, If the two selected elements haven't similar element, the selected element before two elements will be added to compare.
            if (CntLeftClick == 2)
            {
                CntLeftClick = 0;
                RequestSelection = this.Document.Selection;
            }
            else
            {
                RequestSelection = this.Document.Selection;
            }

            if (RequestSelection.Text.Trim() != "")
            {
                // Highlight by background thread.
                ThreadStartHighlighting();
            }
        }

        void VisualElement_PreviewMouseDown(object sender, RoutedEventArgs e)
        {
            // Double click
            if ((e as MouseButtonEventArgs).ClickCount == 2 && (e as MouseButtonEventArgs).LeftButton == MouseButtonState.Pressed)
            {
                CntLeftClick = 2;
                return;
            }
        }

        void GetRootElement()
        {
            // When the target file is edited and not saved, the position of selection is different from befrore.
            var currentTextDoc = Document.Object("TextDocument");
            var tmpSource = currentTextDoc.StartPoint.CreateEditPoint().GetText(currentTextDoc.EndPoint);

            TimeWatch.Init();
            // If the source code was changed, the new one must be used in the next processing.
            if (SourceCode == null || SourceCode != tmpSource)
            {
                SourceCode = tmpSource;
                TimeWatch.Start();
                // If the converting errors, the exception will be catched.
                RootElement = Processor.GenerateXml(SourceCode, true);
                TimeWatch.Stop("GenerateXml");

                //       TokenElements = RootElement.Descendants("TOKEN").ToList();
            }
        }

        void HighlightSimilarElements() {
            
            try
            {
                if (!IsShiftDown)
                {
                    GetRootElement();
                }
                else {
                    IsShiftDown = false;
                }

                var currentSelection = RequestSelection;

                // Validation Check
                if (!IsValidSelection())
                {
                    return;
                }

                var currentStart = ConvertToPosition(RequestSelection.TopPoint);
                var currentEnd = ConvertToPosition(RequestSelection.BottomPoint);
                CurrentWordForCheck = new SnapshotSpan(currentStart, currentEnd);

                var currentRange = GetCodeRangeBySelection(CurrentWordForCheck);
                    
                TimeWatch.Start();
                var currentElement = currentRange.FindOutermostElement(RootElement);
                TimeWatch.Stop("FindOutermostElement");

                BuildLocationsFromTwoElements(currentRange, currentElement);

                if (Locations.Count == 2)
                {

                    // Get the similar Elements.
                    var ret = Inferrer.GetSimilarElements(Processor, Locations,
                            RootElement);

                    TimeWatch.Start();
                    // If no similar element is found then nothing will be highlighted.
                    if (ret.Count() == 0 || ret.First().Item1 == 0)
                    {
                        HaveSimilarElements = false;
                        // The element selected before current two element will be added to compare.
                        if (PreLocationInfo.XElement != null)
                        {
                            var tmpLocationInfo = Locations[0];
                            Locations[0] = PreLocationInfo;

                            // Get the similar Elements.
                            ret = Inferrer.GetSimilarElements(Processor, Locations,
                                    RootElement);
                            
                            // If no similar element is found then nothing will be highlighted.
                            if (ret.Count() == 0 || ret.First().Item1 == 0)
                            {
                                Locations[0] = tmpLocationInfo;
                                return;
                            }                            
                        }
                        else
                        {
                            return;
                        }
                    }

                    // Highlight operation.
                    Highlight(currentSelection, ret);
                }
            }
            catch (Exception exc)
            {
                Debug.Write(exc.ToString());
            }
        }

        void BuildLocationsFromTwoElements(CodeRange currentRange, XElement currentElement) {

            var tmpLocationInfo = new LocationInfo
            {
                CodeRange = currentRange,
                XElement = currentElement,
                IsNeedFix = NeedFixCheck(currentElement),
            }; 

            if (Locations.Count == 2 && HaveSimilarElements)
            {
                // Save the second element when the current two elements have similar element.
                PreLocationInfo = Locations[1];
                Locations.Clear();
            }
            else if (Locations.Count == 2 && !HaveSimilarElements) {
                // Save the first element when the current two elements have not similar element.
                PreLocationInfo = Locations[0];

                Locations[0] = tmpLocationInfo;
                Locations.Reverse();
                return;
            }

            Locations.Add(tmpLocationInfo);

        }

        void Highlight(TextSelection currentSelection, IEnumerable<Tuple<int, CodeRange>> ret)
        {
            // Have the similar elements.
            HaveSimilarElements = true;

            var currentStart = CurrentWordForCheck.Start;
            var currentEnd = CurrentWordForCheck.End;

            if (Locations[0].IsNeedFix || Locations[1].IsNeedFix)
            {
                FixKit = Tuple.Create(RegexNeedFix,
                                    Tuple.Create(startOffset, endOffset));
                if (RegexNeedFix.IsMatch(CurrentWordForCheck.GetText()))
                {
                    currentStart += 1;
                    currentEnd -= 1;
                }
            }
            else
            {
                FixKit = null;
            }

            NewSpanAll = new List<SnapshotSpan>();
            CurrentSelectNum = 0;

            Parallel.ForEach(ret, tuple =>
            {
                lock (buildLock)
                {
                    // Build the data collecton of the similar elements.
                    BuildSimilarElementsCollection(tuple);
                }
            });

            TimeWatch.Stop("BuildSimilarElementsCollection");

            if (NewSpanAll.Count == 0)
            {
                return;
            }

            NormalizedSnapshotSpanCollection wordSpan = new NormalizedSnapshotSpanCollection(NewSpanAll);            

            // TODO if the elements of a line is bigger than 1, the position need to fix.
            var curSelection = wordSpan.AsParallel().Select((item, index) => new { Item = item, Index = index })
                    .First(sel => (sel.Item.Start <= currentStart &&
                    sel.Item.End >= currentEnd));

            if (curSelection != null)
            {
                CurrentSelectNum = curSelection.Index;
                // TODO temp  added
                TMPCurrentSelectNum = CurrentSelectNum;
            }

            // If another change hasn't happened, do a real update
            if (currentSelection == RequestSelection)
                SynchronousUpdate(currentSelection, wordSpan, CurrentWordForCheck);
        }

        // When the selected element does not contain the double quotation marks, 
        // but the token node contains them, 
        // the double quotation marks will be cutted when highlighting.
        bool NeedFixCheck(XElement currentElement)
        {
            // When selected word is between double quotation marks.
            var tokenElements = currentElement.DescendantsAndSelf().Where(el => el.IsToken()).ToList();
            if (tokenElements.Count() == 1 && tokenElements[0].TokenText().Length != RequestSelection.Text.Length
                    && RegexNeedFix.IsMatch(tokenElements[0].TokenText()))
            {
                return true;
            }
            return false;
        }

        bool IsValidSelection()
        {
            // Repeat check   
            if (this.CurrentWordForCheck != null)
            {
                var currentStart = ConvertToPosition(RequestSelection.TopPoint);
                var currentEnd = ConvertToPosition(RequestSelection.BottomPoint);

                // The select operation will be ignored if the selection is same with before.
                if (Locations != null && Locations.Count<LocationInfo>() == 1 && 
                    currentStart.Position == ((SnapshotSpan)this.CurrentWordForCheck).Start.Position &&
                    currentEnd.Position == ((SnapshotSpan)this.CurrentWordForCheck).End.Position)
                {
                    return false;
                }
            }

            return true;
        }

        CodeRange GetCodeRangeBySelection(SnapshotSpan currentWord)
        {
            return CodeRange.ConvertFromIndicies(SourceCode, currentWord.Start.Position, currentWord.End.Position);
        }

        // Convert TextSelection to SnapshotPoint
        // AbsoluteCharOffset count line break as 1 character.
        // Line and LineCharOffset begin at one.
        internal SnapshotPoint ConvertToPosition(TextPoint selectPoint)
        {
            int lineNum = selectPoint.Line - 1;
            int offset = selectPoint.LineCharOffset - 1;
            return this.View.TextSnapshot.GetLineFromLineNumber(lineNum).Start + offset;
        }

        // The position data will be converted from SnapshotPoint to TextSelection.
        internal int ConvertToCharOffset(SnapshotPoint point)
        {
            int lineNum = this.View.TextSnapshot.GetLineNumberFromPosition(point.Position);
            return point.Position - lineNum + 1;
        }

        void BuildSimilarElementsCollection(Tuple<int, CodeRange> tuple)
        {
            // Build the collecton of similar elements.
            var startAndEnd = tuple.Item2.ConvertToIndicies(SourceCode);

            SnapshotPoint tmpStart;
            SnapshotPoint tmpEnd;
            if (FixKit != null && FixKit.Item1.IsMatch(SourceCode.Substring(startAndEnd.Item1, startAndEnd.Item2 - startAndEnd.Item1)))
            {
                tmpStart = new SnapshotPoint(this.View.TextSnapshot, startAndEnd.Item1 + FixKit.Item2.Item1);
                tmpEnd = new SnapshotPoint(this.View.TextSnapshot, startAndEnd.Item2 - FixKit.Item2.Item2);
            }
            else {
                tmpStart = new SnapshotPoint(this.View.TextSnapshot, startAndEnd.Item1);
                tmpEnd = new SnapshotPoint(this.View.TextSnapshot, startAndEnd.Item2);
            }

            var s_span = new SnapshotSpan(tmpStart, tmpEnd);

            NewSpanAll.Add(s_span);
        }

        void SynchronousUpdate(TextSelection CurrentSelection, NormalizedSnapshotSpanCollection newSpans, SnapshotSpan? newCurrentWord)
        {
            lock (updateLock)
            {
                if (CurrentSelection != RequestSelection)
                    return;

                WordSpans = newSpans;
                CurrentWord = newCurrentWord;

                TmpWordSpans = WordSpans;
                var tempEvent = TagsChanged;
                if (tempEvent != null)
                    tempEvent(this, new SnapshotSpanEventArgs(new SnapshotSpan(SourceBuffer.CurrentSnapshot, 0, SourceBuffer.CurrentSnapshot.Length)));
            }
        }

        public event EventHandler<SnapshotSpanEventArgs> TagsChanged;

        public IEnumerable<ITagSpan<HLTextTag>> GetTags(NormalizedSnapshotSpanCollection spans)
        {
            if (CurrentWord == null)
                yield break;

            // Hold on to a "snapshot" of the word spans and current word, so that we maintain the same
            // collection throughout
            var currentWord = CurrentWord.Value;
            var wordSpans = WordSpans;

            if (spans.Count == 0 || WordSpans.Count == 0)
                yield break;

            // If the requested snapshot isn't the same as the one our words are on, translate our spans to the expected snapshot
            if (spans[0].Snapshot != wordSpans[0].Snapshot)
            {
                wordSpans = new NormalizedSnapshotSpanCollection(
                    wordSpans.Select(span => span.TranslateTo(spans[0].Snapshot, SpanTrackingMode.EdgeExclusive)));

                TmpWordSpans = wordSpans;
                currentWord = currentWord.TranslateTo(spans[0].Snapshot, SpanTrackingMode.EdgeExclusive);
            }

            // First, yield back the word the cursor is under (if it overlaps)
            // Note that we'll yield back the same word again in the wordspans collection;
            // the duplication here is expected.
            // It's not necessary for current needs.
            if (spans.OverlapsWith(new NormalizedSnapshotSpanCollection(currentWord)))
                yield return new TagSpan<HLTextTag>(currentWord, new HLTextTag());
            
            // Second, yield all the other words in the file
            foreach (SnapshotSpan span in NormalizedSnapshotSpanCollection.Overlap(spans, wordSpans))
            {
                // TODO: Maybe the next element will be determined by the cursor in new screen.                
                //var curSelection = wordSpans.AsParallel().Select((item, index) => new { Item = item, Index = index })
                //                .First(sel => (sel.Item.Start <= span.Start &&
                //                sel.Item.End >= span.End));
                
                //if (curSelection != null)
                //{
                //    var tmpindex = curSelection.Index;
                //    // If the new span which will be displayed is a long distance from the last selected element.
                //    if (tmpindex - CurrentSelectNum > 10 || CurrentSelectNum - tmpindex > 10)
                //        TMPCurrentSelectNum = tmpindex;
                //    else if ( tmpindex - CurrentSelectNum < 3 || CurrentSelectNum - tmpindex < 3) {
                //        CurrentSelectNum = TMPCurrentSelectNum;
                //    }
                //}
                yield return new TagSpan<HLTextTag>(span, new HLTextTag());
            }            
        }

        private void MoveSelection(string selectType)
        {
            bool blEdge = false;
            if (TMPCurrentSelectNum != CurrentSelectNum)
            {
                CurrentSelectNum = TMPCurrentSelectNum;
            }
            if (selectType == "NEXT")
            {
                CurrentSelectNum += 1;
                // When the current selected element is the last one, the NEXT move operation will be ignored.
                if (TmpWordSpans.Count() <= CurrentSelectNum)
                {
                    CurrentSelectNum = TmpWordSpans.Count() - 1;
                    blEdge = true;
                }
            }
            else if (selectType == "PREV")
            {
                CurrentSelectNum -= 1;
                // When the current selected element is the first one, the PREV move operation will be ignored.
                if (CurrentSelectNum < 0)
                {
                    CurrentSelectNum = 0;
                    blEdge = true;
                }
            }
            TMPCurrentSelectNum = CurrentSelectNum;

            if (blEdge) return;

            var selected = this.Document.Selection;
            if (selected != null)
            {
                // Get the position data of the similar element.
                var newSpan = TmpWordSpans.ElementAt(CurrentSelectNum);
                var newStartOffset = ConvertToCharOffset(newSpan.Start);
                // Make the similar element highlighted.
                selected.MoveToAbsoluteOffset(newStartOffset, false);
                selected.MoveToAbsoluteOffset(newStartOffset + newSpan.Length, true);
            }
        }

        void ThreadStartHighlighting(bool isBackground = true)
        {
            if (isBackground)
            {
                this.listenThread = new System.Threading.Thread(this.HighlightSimilarElements);
                this.listenThread.IsBackground = true;
                this.listenThread.Start();
            }
            else
            {
                HighlightSimilarElements();
            }
        }
    }
}