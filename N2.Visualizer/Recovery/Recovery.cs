﻿#define DebugOutput
using ICSharpCode.AvalonEdit.Editing;
using N2.Internal;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Diagnostics;

#if N2RUNTIME
namespace N2.Strategies
#else
// ReSharper disable once CheckNamespace
namespace N2.DebugStrategies
#endif
{
  using ParserData = Tuple<int, int, List<ParsedStateInfo>>;
  using ReportData = Action<RecoveryResult, List<RecoveryResult>, List<RecoveryResult>, List<RecoveryStackFrame>>;

  public class Recovery
  {
    public ReportData ReportResult;

    public Recovery(ReportData reportResult)
    {
      ReportResult = reportResult;
    }

    private static void ClearAndCollectFrames(RecoveryStackFrame frame, List<RecoveryStackFrame> allRecoveryStackFrames)
    {
      if (frame.Depth != -1)
      {
        allRecoveryStackFrames.Add(frame);
        frame.Depth = -1;
        foreach (var parent in frame.Parents)
          ClearAndCollectFrames(parent, allRecoveryStackFrames);
      }
    }
    private static void UpdateFrameDepth(RecoveryStackFrame frame)
    {
      foreach (var parent in frame.Parents)
        if (parent.Depth <= frame.Depth + 1)
        {
          parent.Depth = frame.Depth + 1;
          UpdateFrameDepth(parent);
        }
    }

    private static List<RecoveryStackFrame> PrepareRecoveryStacks(ICollection<RecoveryStackFrame> heads)
    {
      var allRecoveryStackFrames = new List<RecoveryStackFrame>();

      foreach (var stack in heads)
        ClearAndCollectFrames(stack, allRecoveryStackFrames);
      foreach (var stack in heads)
        stack.Depth = 0;
      foreach (var stack in heads)
        UpdateFrameDepth(stack);

      Sort(allRecoveryStackFrames);

      for (int i = 0; i < allRecoveryStackFrames.Count; ++i)
      {
        var frame = allRecoveryStackFrames[i];
        frame.Index = i;
        frame.Best = false;
        frame.Children.Clear();
      }

      foreach (var frame in allRecoveryStackFrames)
        foreach (var parent in frame.Parents)
        {
          if (parent.Children.Contains(frame))
            Debug.Assert(false);

          parent.Children.Add(frame);
        }

      return allRecoveryStackFrames;
    }

    private static void Sort(List<RecoveryStackFrame> allRecoveryStackFrames)
    {
      allRecoveryStackFrames.Sort((l, r) => l.Depth.CompareTo(r.Depth));
    }

    public virtual int Strategy(Parser parser)
    {
      var failPos = parser.MaxFailPos;
      var curTextPos = failPos;
      var text = parser.Text;

      Debug.Assert(parser.RecoveryStacks.Count > 0);

      var frames = PrepareRecoveryStacks(parser.RecoveryStacks);
      List<RecoveryStackFrame> bestFrames = new List<RecoveryStackFrame>();
      for (;curTextPos < text.Length && bestFrames.Count == 0; curTextPos++)
      {
        var newFrames = new HashSet<RecoveryStackFrame>(frames);
        foreach (var frame in frames)
          if (frame.Depth == 0)
            FindSpeculativeFrames(newFrames, parser, frame, failPos, curTextPos);

        var allFrames = PrepareRecoveryStacks(newFrames);

        foreach (var frame in allFrames)
        {
          if (frame.Depth == 0)
          {
            if (!InitFrame(parser, frame, true, failPos, curTextPos))
            {
              frame.StartState = frame.FailState;
              frame.StartParsePos = curTextPos;
              frame.EndParsePos = -1;
              frame.MaxFailPos = failPos;
            }
          }
          else
            ParseNonTopFrames(parser, curTextPos, failPos, frame);
        }

        FindBestFrames(bestFrames, allFrames);
      }

      return -1;
    }

    private void FindBestFrames(List<RecoveryStackFrame> bestFrames, List<RecoveryStackFrame> allFrames)
    {
      bestFrames.Clear();

      for (int i = allFrames.Count - 1; i >= 0; i--)
      {
        var frame = allFrames[i];

        if (frame.Parents.Count == 0)
          frame.Best = true;

        if (frame.Best)
        {
          var count = frame.Children.Count;
          if (count == 0)
          {
            if (frame.EndParsePos >= 0 || frame.MaxFailPos > frame.StartParsePos)
              bestFrames.Add(frame);
          }
          else if (count == 1)
            frame.Children[0].Best = true;
          else
          {
            var candidates = frame.Children;
            var res1 = Filter(candidates, c => c.EndParsePos);
            var res2 = Filter(res1, c => c.EndParsePos >= 0 ? int.MaxValue : c.MaxFailPos);
            var g = res2.GroupBy(c => Tuple.Create(c.RecoveryRuleParser, c.RuleId));
            var res3 = g.SelectMany(e => Filter(e.ToList(), c => c.FailState)).ToList();
            var res4 = (res3.Any(c => c.IsSpeculative) && !res3.All(c => c.IsSpeculative)) ? res3.Where(c => !c.IsSpeculative) : res3;
            foreach (var frame2 in res4)
              frame2.Best = true;
          }
        }
      }
    }

    private static List<RecoveryStackFrame> Filter(List<RecoveryStackFrame> candidates, Func<RecoveryStackFrame, int> selector)
    {
      var max1 = candidates.Max(selector);
      var res2 = candidates.Where(c => selector(c) == max1);
      return res2.ToList();
    }

    private static void ParseNonTopFrames(Parser parser, int curTextPos, int failPos, RecoveryStackFrame frame)
    {
      int startParsePos = curTextPos;
      int maxFailPos    = failPos;

      foreach (var child in frame.Children)
      {
        startParsePos = Math.Max(startParsePos, child.EndParsePos);
        maxFailPos    = Math.Max(maxFailPos, child.MaxFailPos);
      }
      var state = frame.GetNextState(frame.FailState);
      if (state == -1)
      {
        frame.StartState    = -1;
        frame.StartParsePos = startParsePos;
        frame.EndParsePos   = startParsePos;
        frame.MaxFailPos    = maxFailPos;
      }
      else
      {
        frame.StartState    = -2; // не начинало парситься, т.е. не нашло ни одного состояния с которого возможно допарсивание
        frame.StartParsePos = startParsePos;
        frame.EndParsePos   = -1;
        frame.MaxFailPos    = maxFailPos;

        for (; state >= 0; state = frame.GetNextState(state))
        {
          parser.MaxFailPos = failPos;
          var parsedStates  = new List<ParsedStateInfo>();
          var pos           = frame.TryParse(state, startParsePos, true, parsedStates, parser);

          if (pos > 0)
          {
            frame.StartState    = state;
            frame.StartParsePos = startParsePos;
            frame.EndParsePos   = pos;
            frame.MaxFailPos    = Math.Max(parser.MaxFailPos, maxFailPos);
            break;
          }
        }
      }
    }

    private bool InitFrame(Parser parser, RecoveryStackFrame frame, bool continueList, int failPos, int curTextPos)
    {
      for (var state = frame.FailState; state >= 0; state = frame.GetNextState(state))
      {
        parser.MaxFailPos = failPos;
        var parsedStates = new List<ParsedStateInfo>();
        var pos = frame.TryParse(state, curTextPos, continueList, parsedStates, parser);
        if (NonVoidParsed(frame, curTextPos, pos, parsedStates, parser))
        {
          frame.StartState = state;
          frame.StartParsePos = curTextPos;
          frame.EndParsePos = pos;
          frame.MaxFailPos = parser.MaxFailPos;
          return true;
        }
      }
      return false;
    }

    private bool NonVoidParsed(RecoveryStackFrame frame, int curTextPos, int pos, List<ParsedStateInfo> parsedStates, Parser parser)
    {
      var lastPos = Math.Max(pos, parser.MaxFailPos);
      return lastPos > curTextPos && lastPos - curTextPos > ParsedSpacesLen(frame, parsedStates)
          || parsedStates.Count > 0 && HasParsedStaets(frame, parsedStates);
    }

    private void FindSpeculativeFrames(HashSet<RecoveryStackFrame> newFrames, Parser parser, RecoveryStackFrame frame, int failPos, int curTextPos)
    {
      if (frame.IsTokenRule)
        return;

      if (InitFrame(parser, frame, false, failPos, curTextPos))
      {
        newFrames.Add(frame);
        return;
      }

      if (!frame.IsPrefixParsed) // пытаемся восстановить пропущенный разделитель списка
      {
        var separatorFrame = frame.GetLoopBodyFrameForSeparatorState(curTextPos, parser);

        if (separatorFrame != null)
        {
          // Нас просят попробовать востановить отстуствующий разделитель цикла. Чтобы знать, нужно ли это дела, или мы
          // имеем дело с банальным концом цикла мы должны
          Debug.Assert(separatorFrame.Parents.Count == 1);
          var newFramesCount = newFrames.Count;
          FindSpeculativeFrames(newFrames, parser, separatorFrame, failPos, curTextPos);
          if (newFrames.Count > newFramesCount)
            return;
        }
      }

      for (var state = frame.FailState; state >= 0; state = frame.GetNextState(state))
        FindSpeculativeSubframes(newFrames, parser, frame, curTextPos, state);
    }

    protected virtual void FindSpeculativeSubframes(HashSet<RecoveryStackFrame> newFrames, Parser parser, RecoveryStackFrame frame, int curTextPos, int state)
    {
      foreach (var subFrame in frame.GetSpeculativeFramesForState(curTextPos, parser, state))
      {
        if (subFrame.IsTokenRule)
          continue;

        if (!newFrames.Add(subFrame))
          continue;

        FindSpeculativeSubframes(newFrames, parser, subFrame, curTextPos, subFrame.FailState);
      }
    }

    // ReSharper disable once ParameterTypeCanBeEnumerable.Local
    private static bool HasParsedStaets(RecoveryStackFrame frame, List<ParsedStateInfo> parsedStates)
    {
// ReSharper disable once LoopCanBeConvertedToQuery
      foreach (var parsedState in parsedStates)
      {
        if (!frame.IsVoidState(parsedState.State) && parsedState.Size > 0)
          return true;
      }
      return false;
    }

// ReSharper disable once ParameterTypeCanBeEnumerable.Local
    private static int ParsedSpacesLen(RecoveryStackFrame frame, List<ParsedStateInfo> parsedStates)
    {
      var sum = 0;
// ReSharper disable once LoopCanBeConvertedToQuery
      foreach (var parsedState in parsedStates)
        sum += !frame.IsVoidState(parsedState.State) ? 0 : parsedState.Size;
      return sum;
    }

    protected virtual int ContinueParse(int startTextPos, RecoveryStackFrame recoveryStack, Parser parser, bool trySkipStates)
    {
      return ContinueParseImpl(startTextPos, recoveryStack, parser, trySkipStates);
    }

    protected int ContinueParseImpl(int curTextPos, RecoveryStackFrame recoveryStack, Parser parser, bool trySkipStates)
    {
      var parents = recoveryStack.Parents;

      if (parents.Count == 0)
        return curTextPos;

      var parsedStates = new List<ParsedStateInfo>();
      var results = new List<Tuple<int, RecoveryStackFrame, List<ParsedStateInfo>>>();
      var bestPos = curTextPos;
      foreach (var stackFrame in parents)
      {
        var state = stackFrame.FailState;
        do
        {
          state = stackFrame.GetNextState(state);
          var pos = stackFrame.TryParse(state, curTextPos, true, parsedStates, parser);

          if (pos > bestPos)
          {
            results.Clear();
            bestPos = pos;
          }
          if (pos >= bestPos)
          {
            if (pos > 1)
            { 
            }
            results.Add(Tuple.Create(pos, stackFrame, parsedStates));
            break;
          }
        }
        while (state >= 0);
      }

// ReSharper disable once LoopCanBeConvertedToQuery
      foreach (var result in results)
      {
        var pos = ContinueParseImpl(result.Item1, result.Item2, parser, trySkipStates);
        if (pos > bestPos)
          bestPos = pos;
      }

      if (bestPos > curTextPos)
        return bestPos;

      return Math.Max(parser.MaxFailPos, curTextPos);
    }

    protected virtual int TryParse(Parser parser, RecoveryStackFrame recoveryStack, int curTextPos, int state, out List<ParsedStateInfo> parsedStates)
    {
      parsedStates = new List<ParsedStateInfo>();
      return recoveryStack.TryParse(state, curTextPos, false, parsedStates, parser);
    }

    private void FixAst(Parser parser)
    {
    //  // TODO: Надо переписать. Пока закоментил.
    //  Debug.Assert(_bestResult != null);

    //  var frame = _bestResult.Stack.Head;

    //  if (frame.AstStartPos < 0)
    //    Debug.Assert(frame.AstPtr >= 0);

    //  var error = new ParseErrorData(new NToken(_bestResult.FailPos, _bestResult.StartPos), _bestResults.ToArray());
    //  var errorIndex = parser.ErrorData.Count;
    //  parser.ErrorData.Add(error);

    //  frame.RuleParser.PatchAst(_bestResult.StartPos, _bestResult.StartState, errorIndex, _bestResult.Stack, parser);

    //  for (var stack = _bestResult.Stack.Tail as RecoveryStack; stack != null; stack = stack.Tail as RecoveryStack)
    //  {
    //    if (stack.Head.RuleParser is ExtensibleRuleParser)
    //      continue;
    //    Debug.Assert(stack.Head.FailState >= 0);
    //    stack.Head.RuleParser.PatchAst(stack.Head.AstStartPos, -2, -1, stack, parser);
    //  }
    }

  }
}
