// getline.cs: A command line editor
//
// Authors:
//   Miguel de Icaza (miguel@novell.com)
//
// Copyright 2008 Novell, Inc.
//
// Dual-licensed under the terms of the MIT X11 license or the
// Apache License 2.0
//
// USE -define:DEMO to build this as a standalone file and test it
//
// TODO:
//    Enter an error (a = 1);  Notice how the prompt is in the wrong line
//    This is caused by Stderr not being tracked by System.Console.
//    Completion support
//    Why is Thread.Interrupt not working?   Currently I resort to Abort which is too much.
//
// Limitations in System.Console:
//    Console needs SIGWINCH support of some sort
//    Console needs a way of updating its position after things have been written
//    behind its back (P/Invoke puts for example).
//    System.Console needs to get the DELETE character, and report accordingly.

using System;
using System.IO;
using System.Text;
using System.Threading;

namespace WebProvisioningTemplate.Console
{
    public class LineEditor
    {
        public delegate Completion AutoCompleteHandler(string text, int pos);

        #region Constants

        private static Handler[] _handlers;

        #endregion

        #region Fields

        // Our object that tracks history
        private readonly History _history;

        // The prompt specified, and the prompt shown to the user.

        // The text as it is rendered (replaces (char)1 with ^A on display for example).
        private readonly StringBuilder _renderedText;

        /// <summary>
        ///     Invoked when the user requests auto-completion using the tab character.
        /// </summary>
        /// <remarks>
        ///     The result is null for no values found, an array with a single
        ///     string, in that case the string should be the text to be inserted
        ///     for example if the word at position is "T", the result for a completion
        ///     of "ToString" should be "oString", not "ToString".
        ///     When there are multiple results, the result should be the full
        ///     text.
        /// </remarks>
        public AutoCompleteHandler AutoCompleteEvent;

        // The current cursor position, indexes into "text", for an index
        // into rendered_text, use TextToRenderPos.
        private int _cursor;

        // If we are done editing, this breaks the interactive loop
        private bool _done;

        // The thread where the Editing started taking place
        private Thread _editThread;

        // The row where we started displaying data.
        private int _homeRow;

        // The contents of the kill buffer (cut/paste in Emacs parlance)
        private string _killBuffer = string.Empty;

        // Used to implement the Kill semantics (multiple Alt-Ds accumulate)
        private KeyHandler _lastHandler;
        private string _lastSearch;

        // The position where we found the match.
        private int _matchAt;

        // The maximum length that has been displayed on the screen
        private int _maxRendered;

        // The string being searched for
        private string _search;

        // whether we are searching (-1= reverse; 0 = no; 1 = forward)
        private int _searching;
        private string _shownPrompt;

        // The text being edited.
        private StringBuilder _text;

        #endregion

        #region Properties

        private string Prompt { get; set; }

        private int LineCount
        {
            get
            {
                return (_shownPrompt.Length + _renderedText.Length) / System.Console.WindowWidth;
            }
        }

        public bool TabAtStartCompletes { get; set; }

        #endregion

        #region Constructors

        public LineEditor(string name) : this(name, 10)
        {
        }

        public LineEditor(string name, int histsize)
        {
            _handlers = new[]
            {
                new Handler(ConsoleKey.Home, CmdHome), new Handler(ConsoleKey.End, CmdEnd), new Handler(ConsoleKey.LeftArrow, CmdLeft), new Handler(ConsoleKey.RightArrow, CmdRight),
                new Handler(ConsoleKey.UpArrow, CmdHistoryPrev), new Handler(ConsoleKey.DownArrow, CmdHistoryNext), new Handler(ConsoleKey.Enter, CmdDone),
                new Handler(ConsoleKey.Backspace, CmdBackspace), new Handler(ConsoleKey.Delete, CmdDeleteChar), new Handler(ConsoleKey.Tab, CmdTabOrComplete),

                // Emacs keys
                Handler.Control('A', CmdHome), Handler.Control('E', CmdEnd), Handler.Control('B', CmdLeft), Handler.Control('F', CmdRight), Handler.Control('P', CmdHistoryPrev),
                Handler.Control('N', CmdHistoryNext), Handler.Control('K', CmdKillToEOF), Handler.Control('Y', CmdYank), Handler.Control('D', CmdDeleteChar),
                Handler.Control('L', CmdRefresh), Handler.Control('R', CmdReverseSearch), Handler.Control('G', delegate { }), Handler.Alt('B', ConsoleKey.B, CmdBackwardWord),
                Handler.Alt('F', ConsoleKey.F, CmdForwardWord), Handler.Alt('D', ConsoleKey.D, CmdDeleteWord), Handler.Alt((char)8, ConsoleKey.Backspace, CmdDeleteBackword),

                // quote
                Handler.Control('Q', delegate { HandleChar(System.Console.ReadKey(true).KeyChar); })
            };

            _renderedText = new StringBuilder();
            _text = new StringBuilder();

            _history = new History(name, histsize);
        }

        #endregion

        #region Non-Public Methods

        private void CmdDebug()
        {
            _history.Dump();
            System.Console.WriteLine();
            Render();
        }

        private void Render()
        {
            System.Console.Write(_shownPrompt);
            System.Console.Write(_renderedText);

            int max = Math.Max(_renderedText.Length + _shownPrompt.Length, _maxRendered);

            for (int i = _renderedText.Length + _shownPrompt.Length; i < _maxRendered; i++)
                System.Console.Write(' ');
            _maxRendered = _shownPrompt.Length + _renderedText.Length;

            // Write one more to ensure that we always wrap around properly if we are at the
            // end of a line.
            System.Console.Write(' ');

            UpdateHomeRow(max);
        }

        private void UpdateHomeRow(int screenpos)
        {
            int lines = 1 + (screenpos / System.Console.WindowWidth);

            _homeRow = System.Console.CursorTop - (lines - 1);
            if (_homeRow < 0)
                _homeRow = 0;
        }

        private void RenderFrom(int pos)
        {
            int rpos = TextToRenderPos(pos);
            int i;

            for (i = rpos; i < _renderedText.Length; i++)
                System.Console.Write(_renderedText[i]);

            if ((_shownPrompt.Length + _renderedText.Length) > _maxRendered)
                _maxRendered = _shownPrompt.Length + _renderedText.Length;
            else
            {
                int max_extra = _maxRendered - _shownPrompt.Length;
                for (; i < max_extra; i++)
                    System.Console.Write(' ');
            }
        }

        private void ComputeRendered()
        {
            _renderedText.Length = 0;

            for (var i = 0; i < _text.Length; i++)
            {
                int c = _text[i];
                if (c < 26)
                {
                    if (c == '\t')
                        _renderedText.Append("    ");
                    else
                    {
                        _renderedText.Append('^');
                        _renderedText.Append((char)(c + 'A' - 1));
                    }
                }
                else
                    _renderedText.Append((char)c);
            }
        }

        private int TextToRenderPos(int pos)
        {
            var p = 0;

            for (var i = 0; i < pos; i++)
            {
                int c;

                c = _text[i];

                if (c < 26)
                {
                    if (c == 9)
                        p += 4;
                    else
                        p += 2;
                }
                else
                    p++;
            }

            return p;
        }

        private int TextToScreenPos(int pos)
        {
            return _shownPrompt.Length + TextToRenderPos(pos);
        }

        private void ForceCursor(int newpos)
        {
            _cursor = newpos;

            int actual_pos = _shownPrompt.Length + TextToRenderPos(_cursor);
            int row = _homeRow + (actual_pos / System.Console.WindowWidth);
            int col = actual_pos % System.Console.WindowWidth;

            if (row >= System.Console.BufferHeight)
                row = System.Console.BufferHeight - 1;
            System.Console.SetCursorPosition(col, row);
        }

        private void UpdateCursor(int newpos)
        {
            if (_cursor == newpos)
                return;

            ForceCursor(newpos);
        }

        private void InsertChar(char c)
        {
            int prev_lines = LineCount;
            _text = _text.Insert(_cursor, c);
            ComputeRendered();
            if (prev_lines != LineCount)
            {
                System.Console.SetCursorPosition(0, _homeRow);
                Render();
                ForceCursor(++_cursor);
            }
            else
            {
                RenderFrom(_cursor);
                ForceCursor(++_cursor);
                UpdateHomeRow(TextToScreenPos(_cursor));
            }
        }

        // Commands
        private void CmdDone()
        {
            _done = true;
        }

        private void CmdTabOrComplete()
        {
            var complete = false;

            if (AutoCompleteEvent != null)
            {
                if (TabAtStartCompletes)
                    complete = true;
                else
                {
                    for (var i = 0; i < _cursor; i++)
                    {
                        if (!char.IsWhiteSpace(_text[i]))
                        {
                            complete = true;
                            break;
                        }
                    }
                }

                if (complete)
                {
                    Completion completion = AutoCompleteEvent(_text.ToString(), _cursor);
                    string[] completions = completion.Result;
                    if (completions == null)
                        return;

                    int ncompletions = completions.Length;
                    if (ncompletions == 0)
                        return;

                    if (completions.Length == 1)
                    {
                        InsertTextAtCursor(completions[0]);
                    }
                    else
                    {
                        int last = -1;

                        for (var p = 0; p < completions[0].Length; p++)
                        {
                            char c = completions[0][p];

                            for (var i = 1; i < ncompletions; i++)
                            {
                                if (completions[i].Length < p)
                                    goto mismatch;

                                if (completions[i][p] != c)
                                {
                                    goto mismatch;
                                }
                            }

                            last = p;
                        }

                        mismatch:
                        if (last != -1)
                        {
                            InsertTextAtCursor(completions[0].Substring(0, last + 1));
                        }

                        System.Console.WriteLine();
                        foreach (string s in completions)
                        {
                            System.Console.Write(completion.Prefix);
                            System.Console.Write(s);
                            System.Console.Write(' ');
                        }

                        System.Console.WriteLine();
                        Render();
                        ForceCursor(_cursor);
                    }
                }
                else
                    HandleChar('\t');
            }
            else
                HandleChar('t');
        }

        private void CmdHome()
        {
            UpdateCursor(0);
        }

        private void CmdEnd()
        {
            UpdateCursor(_text.Length);
        }

        private void CmdLeft()
        {
            if (_cursor == 0)
                return;

            UpdateCursor(_cursor - 1);
        }

        private void CmdBackwardWord()
        {
            int p = WordBackward(_cursor);
            if (p == -1)
                return;
            UpdateCursor(p);
        }

        private void CmdForwardWord()
        {
            int p = WordForward(_cursor);
            if (p == -1)
                return;
            UpdateCursor(p);
        }

        private void CmdRight()
        {
            if (_cursor == _text.Length)
                return;

            UpdateCursor(_cursor + 1);
        }

        private void RenderAfter(int p)
        {
            ForceCursor(p);
            RenderFrom(p);
            ForceCursor(_cursor);
        }

        private void CmdBackspace()
        {
            if (_cursor == 0)
                return;

            _text.Remove(--_cursor, 1);
            ComputeRendered();
            RenderAfter(_cursor);
        }

        private void CmdDeleteChar()
        {
            // If there is no input, this behaves like EOF
            if (_text.Length == 0)
            {
                _done = true;
                _text = null;
                System.Console.WriteLine();
                return;
            }

            if (_cursor == _text.Length)
                return;
            _text.Remove(_cursor, 1);
            ComputeRendered();
            RenderAfter(_cursor);
        }

        private int WordForward(int p)
        {
            if (p >= _text.Length)
                return -1;

            int i = p;
            if (char.IsPunctuation(_text[p]) || char.IsWhiteSpace(_text[p]))
            {
                for (; i < _text.Length; i++)
                {
                    if (char.IsLetterOrDigit(_text[i]))
                        break;
                }

                for (; i < _text.Length; i++)
                {
                    if (!char.IsLetterOrDigit(_text[i]))
                        break;
                }
            }
            else
            {
                for (; i < _text.Length; i++)
                {
                    if (!char.IsLetterOrDigit(_text[i]))
                        break;
                }
            }

            if (i != p)
                return i;

            return -1;
        }

        private int WordBackward(int p)
        {
            if (p == 0)
                return -1;

            int i = p - 1;
            if (i == 0)
                return 0;

            if (char.IsPunctuation(_text[i]) || char.IsSymbol(_text[i]) || char.IsWhiteSpace(_text[i]))
            {
                for (; i >= 0; i--)
                {
                    if (char.IsLetterOrDigit(_text[i]))
                        break;
                }

                for (; i >= 0; i--)
                {
                    if (!char.IsLetterOrDigit(_text[i]))
                        break;
                }
            }
            else
            {
                for (; i >= 0; i--)
                {
                    if (!char.IsLetterOrDigit(_text[i]))
                        break;
                }
            }

            i++;

            if (i != p)
                return i;

            return -1;
        }

        private void CmdDeleteWord()
        {
            int pos = WordForward(_cursor);

            if (pos == -1)
                return;

            string k = _text.ToString(_cursor, pos - _cursor);

            if (_lastHandler == CmdDeleteWord)
                _killBuffer = _killBuffer + k;
            else
                _killBuffer = k;

            _text.Remove(_cursor, pos - _cursor);
            ComputeRendered();
            RenderAfter(_cursor);
        }

        private void CmdDeleteBackword()
        {
            int pos = WordBackward(_cursor);
            if (pos == -1)
                return;

            string k = _text.ToString(pos, _cursor - pos);

            if (_lastHandler == CmdDeleteBackword)
                _killBuffer = k + _killBuffer;
            else
                _killBuffer = k;

            _text.Remove(pos, _cursor - pos);
            ComputeRendered();
            RenderAfter(pos);
        }

        // Adds the current line to the history if needed
        private void HistoryUpdateLine()
        {
            _history.Update(_text.ToString());
        }

        private void CmdHistoryPrev()
        {
            if (!_history.PreviousAvailable())
                return;

            HistoryUpdateLine();

            SetText(_history.Previous());
        }

        private void CmdHistoryNext()
        {
            if (!_history.NextAvailable())
                return;

            _history.Update(_text.ToString());
            SetText(_history.Next());
        }

        private void CmdKillToEOF()
        {
            _killBuffer = _text.ToString(_cursor, _text.Length - _cursor);
            _text.Length = _cursor;
            ComputeRendered();
            RenderAfter(_cursor);
        }

        private void CmdYank()
        {
            InsertTextAtCursor(_killBuffer);
        }

        private void InsertTextAtCursor(string str)
        {
            int prev_lines = LineCount;
            _text.Insert(_cursor, str);
            ComputeRendered();
            if (prev_lines != LineCount)
            {
                System.Console.SetCursorPosition(0, _homeRow);
                Render();
                _cursor += str.Length;
                ForceCursor(_cursor);
            }
            else
            {
                RenderFrom(_cursor);
                _cursor += str.Length;
                ForceCursor(_cursor);
                UpdateHomeRow(TextToScreenPos(_cursor));
            }
        }

        private void SetSearchPrompt(string s)
        {
            SetPrompt("(reverse-i-search)`" + s + "': ");
        }

        private void ReverseSearch()
        {
            int p;

            if (_cursor == _text.Length)
            {
                // The cursor is at the end of the string
                p = _text.ToString().LastIndexOf(_search);
                if (p != -1)
                {
                    _matchAt = p;
                    _cursor = p;
                    ForceCursor(_cursor);
                    return;
                }
            }
            else
            {
                // The cursor is somewhere in the middle of the string
                int start = (_cursor == _matchAt) ? _cursor - 1 : _cursor;
                if (start != -1)
                {
                    p = _text.ToString().LastIndexOf(_search, start);
                    if (p != -1)
                    {
                        _matchAt = p;
                        _cursor = p;
                        ForceCursor(_cursor);
                        return;
                    }
                }
            }

            // Need to search backwards in history
            HistoryUpdateLine();
            string s = _history.SearchBackward(_search);
            if (s != null)
            {
                _matchAt = -1;
                SetText(s);
                ReverseSearch();
            }
        }

        private void CmdReverseSearch()
        {
            if (_searching == 0)
            {
                _matchAt = -1;
                _lastSearch = _search;
                _searching = -1;
                _search = string.Empty;
                SetSearchPrompt(string.Empty);
            }
            else
            {
                if (_search == string.Empty)
                {
                    if (_lastSearch != string.Empty && _lastSearch != null)
                    {
                        _search = _lastSearch;
                        SetSearchPrompt(_search);
                        ReverseSearch();
                    }

                    return;
                }

                ReverseSearch();
            }
        }

        private void SearchAppend(char c)
        {
            _search = _search + c;
            SetSearchPrompt(_search);

            // If the new typed data still matches the current text, stay here
            if (_cursor < _text.Length)
            {
                string r = _text.ToString(_cursor, _text.Length - _cursor);
                if (r.StartsWith(_search))
                    return;
            }

            ReverseSearch();
        }

        private void CmdRefresh()
        {
            System.Console.Clear();
            _maxRendered = 0;
            Render();
            ForceCursor(_cursor);
        }

        private void InterruptEdit(object sender, ConsoleCancelEventArgs a)
        {
            // Do not abort our program:
            a.Cancel = true;

            // Interrupt the editor
            _editThread.Abort();
        }

        private void HandleChar(char c)
        {
            if (_searching != 0)
                SearchAppend(c);
            else
                InsertChar(c);
        }

        private void EditLoop()
        {
            ConsoleKeyInfo cki;

            while (!_done)
            {
                ConsoleModifiers mod;

                cki = System.Console.ReadKey(true);
                if (cki.Key == ConsoleKey.Escape)
                {
                    cki = System.Console.ReadKey(true);

                    mod = ConsoleModifiers.Alt;
                }
                else
                    mod = cki.Modifiers;

                var handled = false;

                foreach (Handler handler in _handlers)
                {
                    ConsoleKeyInfo t = handler.CKI;

                    if (t.Key == cki.Key && t.Modifiers == mod)
                    {
                        handled = true;
                        handler.KeyHandler();
                        _lastHandler = handler.KeyHandler;
                        break;
                    }

                    if (t.KeyChar == cki.KeyChar && t.Key == ConsoleKey.Zoom)
                    {
                        handled = true;
                        handler.KeyHandler();
                        _lastHandler = handler.KeyHandler;
                        break;
                    }
                }

                if (handled)
                {
                    if (_searching != 0)
                    {
                        if (_lastHandler != CmdReverseSearch)
                        {
                            _searching = 0;
                            SetPrompt(Prompt);
                        }
                    }

                    continue;
                }

                if (cki.KeyChar != (char)0)
                    HandleChar(cki.KeyChar);
            }
        }

        private void InitText(string initial)
        {
            _text = new StringBuilder(initial);
            ComputeRendered();
            _cursor = _text.Length;
            Render();
            ForceCursor(_cursor);
        }

        private void SetText(string newtext)
        {
            System.Console.SetCursorPosition(0, _homeRow);
            InitText(newtext);
        }

        private void SetPrompt(string newprompt)
        {
            _shownPrompt = newprompt;
            System.Console.SetCursorPosition(0, _homeRow);
            Render();
            ForceCursor(_cursor);
        }

        #endregion

        #region Public Methods

        public string Edit(string prompt, string initial)
        {
            _editThread = Thread.CurrentThread;
            _searching = 0;
            System.Console.CancelKeyPress += InterruptEdit;

            _done = false;
            _history.CursorToEnd();
            _maxRendered = 0;

            Prompt = prompt;
            _shownPrompt = prompt;
            InitText(initial);
            _history.Append(initial);

            do
            {
                try
                {
                    EditLoop();
                }
                catch (ThreadAbortException)
                {
                    _searching = 0;
                    Thread.ResetAbort();
                    System.Console.WriteLine();
                    SetPrompt(prompt);
                    SetText(string.Empty);
                }
            }
            while (!_done);

            System.Console.WriteLine();
            System.Console.CancelKeyPress -= InterruptEdit;

            //// if (text == null)
            //// {
            ////    history.Close();
            ////    return null;
            //// }

            string result = _text.ToString();
            if (result != string.Empty)
            {
                _history.Accept(result);
                _history.Close();
            }
            else
            {
                _history.RemoveLast();
            }
            
            return result;
        }

        #endregion

        #region Nested types

        public class Completion
        {
            #region Fields

            public string Prefix;
            public string[] Result;

            #endregion

            #region Constructors

            public Completion(string prefix, string[] result)
            {
                Prefix = prefix;
                Result = result;
            }

            #endregion
        }

        private delegate void KeyHandler();

        private struct Handler
        {
            public readonly ConsoleKeyInfo CKI;
            public readonly KeyHandler KeyHandler;

            public Handler(ConsoleKey key, KeyHandler h)
            {
                CKI = new ConsoleKeyInfo((char)0, key, false, false, false);
                KeyHandler = h;
            }

            public Handler(char c, KeyHandler h)
            {
                KeyHandler = h;

                // Use the "Zoom" as a flag that we only have a character.
                CKI = new ConsoleKeyInfo(c, ConsoleKey.Zoom, false, false, false);
            }

            public Handler(ConsoleKeyInfo cki, KeyHandler h)
            {
                CKI = cki;
                KeyHandler = h;
            }

            public static Handler Control(char c, KeyHandler h)
            {
                return new Handler((char)(c - 'A' + 1), h);
            }

            public static Handler Alt(char c, ConsoleKey k, KeyHandler h)
            {
                var cki = new ConsoleKeyInfo(c, k, false, true, false);
                return new Handler(cki, h);
            }
        }

        // Emulates the bash-like behavior, where edits done to the
        // history are recorded
        private class History
        {
            #region Fields

            private readonly string _histfile;
            private readonly string[] _history;

            private int _cursor;
            private int _count;
            private int _head;
            private int _tail;

            #endregion

            #region Constructors

            public History(string app, int size)
            {
                if (size < 1)
                    throw new ArgumentException("size");

                if (app != null)
                {
                    string dir = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

                    if (!Directory.Exists(dir))
                    {
                        try
                        {
                            Directory.CreateDirectory(dir);
                        }
                        catch
                        {
                            app = null;
                        }
                    }

                    if (app != null)
                        _histfile = Path.Combine(dir, app) + ".history";
                }

                _history = new string[size];
                _head = _tail = _cursor = 0;

                if (File.Exists(_histfile))
                {
                    using (StreamReader sr = File.OpenText(_histfile))
                    {
                        string line;

                        while ((line = sr.ReadLine()) != null)
                        {
                            if (line != string.Empty)
                                Append(line);
                        }
                    }
                }
            }

            #endregion

            #region Public Methods

            public void Close()
            {
                if (_histfile == null)
                    return;

                try
                {
                    using (StreamWriter sw = File.CreateText(_histfile))
                    {
                        int start = (_count == _history.Length) ? _head : _tail;
                        for (int i = start; i < start + _count; i++)
                        {
                            int p = i % _history.Length;
                            sw.WriteLine(_history[p]);
                        }
                    }
                }
                catch
                {
                    // ignore
                }
            }

            // Appends a value to the history
            public void Append(string s)
            {
                _history[_head] = s;
                _head = (_head + 1) % _history.Length;
                if (_head == _tail)
                    _tail = _tail + 1 % _history.Length;
                if (_count != _history.Length)
                    _count++;
            }

            // Updates the current cursor location with the string,
            // to support editing of history items.   For the current
            // line to participate, an Append must be done before.
            public void Update(string s)
            {
                _history[_cursor] = s;
            }

            public void RemoveLast()
            {
                _head = _head - 1;
                if (_head < 0)
                    _head = _history.Length - 1;
            }

            public void Accept(string s)
            {
                int t = _head - 1;
                if (t < 0)
                    t = _history.Length - 1;

                _history[t] = s;
            }

            public bool PreviousAvailable()
            {
                if (_count == 0 || _cursor == _tail)
                    return false;

                return true;
            }

            public bool NextAvailable()
            {
                int next = (_cursor + 1) % _history.Length;
                if (_count == 0 || next >= _head)
                    return false;

                return true;
            }

            // Returns: a string with the previous line contents, or
            // nul if there is no data in the history to move to.
            public string Previous()
            {
                if (!PreviousAvailable())
                    return null;

                _cursor--;
                if (_cursor < 0)
                    _cursor = _history.Length - 1;

                return _history[_cursor];
            }

            public string Next()
            {
                if (!NextAvailable())
                    return null;

                _cursor = (_cursor + 1) % _history.Length;
                return _history[_cursor];
            }

            public void CursorToEnd()
            {
                if (_head == _tail)
                    return;

                _cursor = _head;
            }

            public void Dump()
            {
                System.Console.WriteLine("Head={0} Tail={1} Cursor={2}", _head, _tail, _cursor);
                for (var i = 0; i < _history.Length; i++)
                {
                    System.Console.WriteLine(" {0} {1}: {2}", i == _cursor ? "==>" : "   ", i, _history[i]);
                }
            }

            public string SearchBackward(string term)
            {
                for (var i = 1; i < _count; i++)
                {
                    int slot = _cursor - i;
                    if (slot < 0)
                        slot = _history.Length - 1;
                    if (_history[slot] != null && _history[slot].IndexOf(term) != -1)
                    {
                        _cursor = slot;
                        return _history[slot];
                    }

                    // Will the next hit tail?
                    slot--;
                    if (slot < 0)
                        slot = _history.Length - 1;
                    if (slot == _tail)
                        break;
                }

                return null;
            }

            #endregion
        }

        #endregion
    }
}