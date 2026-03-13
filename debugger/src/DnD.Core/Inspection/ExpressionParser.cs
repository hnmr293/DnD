namespace DnD.Core.Inspection;

/// <summary>
/// Recursive descent parser for debugger evaluation expressions.
/// Produces an AST from expressions like "obj.Name", "a + b", "list[0].ToString()".
/// </summary>
public static class ExpressionParser
{
    public static ExprNode Parse(string expression)
    {
        var tokens = Tokenize(expression);
        var parser = new Parser(tokens);
        var result = parser.ParseExpression();
        if (parser.Position < tokens.Count)
            throw new FormatException($"Unexpected token '{tokens[parser.Position]}' at position {parser.Position}");
        return result;
    }

    #region AST Node Types

    public abstract record ExprNode;
    public record LiteralNode(object? Value, LiteralKind Kind) : ExprNode;
    public record NameNode(string Name) : ExprNode;
    public record MemberAccessNode(ExprNode Object, string MemberName) : ExprNode;
    public record MethodCallNode(ExprNode Object, string MethodName, ExprNode[] Arguments) : ExprNode;
    public record IndexAccessNode(ExprNode Object, ExprNode Index) : ExprNode;
    public record BinaryOpNode(ExprNode Left, BinaryOp Op, ExprNode Right) : ExprNode;
    public record CastNode(string TypeName, ExprNode Operand) : ExprNode;

    public enum LiteralKind { Int, Long, Double, String, Bool, Null }

    public enum BinaryOp
    {
        Add, Sub, Mul, Div, Mod,
        Eq, NotEq, Lt, Gt, LtEq, GtEq,
        And, Or
    }

    #endregion

    #region Tokenizer

    private enum TokenType
    {
        Name, IntLiteral, DoubleLiteral, StringLiteral,
        Dot, LParen, RParen, LBracket, RBracket, Comma,
        Plus, Minus, Star, Slash, Percent,
        EqEq, BangEq, Lt, Gt, LtEq, GtEq,
        AmpAmp, PipePipe, Bang,
        True, False, Null,
    }

    private record Token(TokenType Type, string Text, object? Value = null);

    private static List<Token> Tokenize(string input)
    {
        var tokens = new List<Token>();
        int i = 0;

        while (i < input.Length)
        {
            if (char.IsWhiteSpace(input[i])) { i++; continue; }

            // String literal
            if (input[i] == '"')
            {
                i++;
                var sb = new System.Text.StringBuilder();
                while (i < input.Length && input[i] != '"')
                {
                    if (input[i] == '\\' && i + 1 < input.Length)
                    {
                        i++;
                        sb.Append(input[i] switch { 'n' => '\n', 't' => '\t', '\\' => '\\', '"' => '"', _ => input[i] });
                    }
                    else
                    {
                        sb.Append(input[i]);
                    }
                    i++;
                }
                if (i >= input.Length) throw new FormatException("Unterminated string literal");
                i++; // skip closing "
                tokens.Add(new Token(TokenType.StringLiteral, sb.ToString(), sb.ToString()));
                continue;
            }

            // Numbers
            if (char.IsDigit(input[i]) || (input[i] == '-' && i + 1 < input.Length && char.IsDigit(input[i + 1]) && (tokens.Count == 0 || IsOperatorToken(tokens[^1].Type))))
            {
                var start = i;
                if (input[i] == '-') i++;
                while (i < input.Length && char.IsDigit(input[i])) i++;
                bool isDouble = false;
                if (i < input.Length && input[i] == '.' && i + 1 < input.Length && char.IsDigit(input[i + 1]))
                {
                    isDouble = true;
                    i++;
                    while (i < input.Length && char.IsDigit(input[i])) i++;
                }
                var text = input[start..i];
                if (isDouble)
                    tokens.Add(new Token(TokenType.DoubleLiteral, text, double.Parse(text, System.Globalization.CultureInfo.InvariantCulture)));
                else
                {
                    if (i < input.Length && (input[i] == 'L' || input[i] == 'l'))
                    {
                        i++;
                        tokens.Add(new Token(TokenType.IntLiteral, text, long.Parse(text)));
                    }
                    else
                        tokens.Add(new Token(TokenType.IntLiteral, text, int.Parse(text)));
                }
                continue;
            }

            // Identifiers / keywords
            if (char.IsLetter(input[i]) || input[i] == '_' || input[i] == '$')
            {
                var start = i;
                while (i < input.Length && (char.IsLetterOrDigit(input[i]) || input[i] == '_' || input[i] == '$')) i++;
                var text = input[start..i];
                var type = text switch
                {
                    "true" => TokenType.True,
                    "false" => TokenType.False,
                    "null" => TokenType.Null,
                    _ => TokenType.Name,
                };
                tokens.Add(new Token(type, text, type switch
                {
                    TokenType.True => true,
                    TokenType.False => false,
                    TokenType.Null => null,
                    _ => text,
                }));
                continue;
            }

            // Two-char operators
            if (i + 1 < input.Length)
            {
                var two = input.Substring(i, 2);
                var twoType = two switch
                {
                    "==" => (TokenType?)TokenType.EqEq,
                    "!=" => TokenType.BangEq,
                    "<=" => TokenType.LtEq,
                    ">=" => TokenType.GtEq,
                    "&&" => TokenType.AmpAmp,
                    "||" => TokenType.PipePipe,
                    _ => null
                };
                if (twoType.HasValue)
                {
                    tokens.Add(new Token(twoType.Value, two));
                    i += 2;
                    continue;
                }
            }

            // Single-char operators
            var ch = input[i];
            var singleType = ch switch
            {
                '.' => TokenType.Dot,
                '(' => TokenType.LParen,
                ')' => TokenType.RParen,
                '[' => TokenType.LBracket,
                ']' => TokenType.RBracket,
                ',' => TokenType.Comma,
                '+' => TokenType.Plus,
                '-' => TokenType.Minus,
                '*' => TokenType.Star,
                '/' => TokenType.Slash,
                '%' => TokenType.Percent,
                '<' => TokenType.Lt,
                '>' => TokenType.Gt,
                '!' => TokenType.Bang,
                _ => throw new FormatException($"Unexpected character '{ch}' at position {i}")
            };
            tokens.Add(new Token(singleType, ch.ToString()));
            i++;
        }

        return tokens;
    }

    private static bool IsOperatorToken(TokenType type) => type switch
    {
        TokenType.Plus or TokenType.Minus or TokenType.Star or TokenType.Slash or TokenType.Percent
        or TokenType.EqEq or TokenType.BangEq or TokenType.Lt or TokenType.Gt or TokenType.LtEq or TokenType.GtEq
        or TokenType.LParen or TokenType.Comma or TokenType.LBracket => true,
        _ => false,
    };

    #endregion

    #region Parser

    // Grammar (operator precedence, lowest to highest):
    //   Expression = Or
    //   Or         = And ("||" And)*
    //   And        = Equality ("&&" Equality)*
    //   Equality   = Comparison (("==" | "!=") Comparison)*
    //   Comparison = Addition (("<" | ">" | "<=" | ">=") Addition)*
    //   Addition   = Multiply (("+" | "-") Multiply)*
    //   Multiply   = Unary (("*" | "/" | "%") Unary)*
    //   Unary      = ("(" TypeName ")")? Postfix
    //   Postfix    = Primary ("." Name ("(" Args ")")? | "[" Expr "]")*
    //   Primary    = Literal | Name | "(" Expression ")"

    private class Parser(List<Token> tokens)
    {
        public int Position { get; private set; }

        private Token? Peek() => Position < tokens.Count ? tokens[Position] : null;
        private Token Advance() => tokens[Position++];
        private bool Match(TokenType type) { if (Peek()?.Type == type) { Position++; return true; } return false; }
        private Token Expect(TokenType type)
        {
            var tok = Peek() ?? throw new FormatException($"Unexpected end of expression, expected {type}");
            if (tok.Type != type) throw new FormatException($"Expected {type}, got '{tok.Text}'");
            Position++;
            return tok;
        }

        public ExprNode ParseExpression() => ParseOr();

        private ExprNode ParseOr()
        {
            var left = ParseAnd();
            while (Match(TokenType.PipePipe))
            {
                var right = ParseAnd();
                left = new BinaryOpNode(left, BinaryOp.Or, right);
            }
            return left;
        }

        private ExprNode ParseAnd()
        {
            var left = ParseEquality();
            while (Match(TokenType.AmpAmp))
            {
                var right = ParseEquality();
                left = new BinaryOpNode(left, BinaryOp.And, right);
            }
            return left;
        }

        private ExprNode ParseEquality()
        {
            var left = ParseComparison();
            while (true)
            {
                if (Match(TokenType.EqEq)) left = new BinaryOpNode(left, BinaryOp.Eq, ParseComparison());
                else if (Match(TokenType.BangEq)) left = new BinaryOpNode(left, BinaryOp.NotEq, ParseComparison());
                else break;
            }
            return left;
        }

        private ExprNode ParseComparison()
        {
            var left = ParseAddition();
            while (true)
            {
                if (Match(TokenType.Lt)) left = new BinaryOpNode(left, BinaryOp.Lt, ParseAddition());
                else if (Match(TokenType.Gt)) left = new BinaryOpNode(left, BinaryOp.Gt, ParseAddition());
                else if (Match(TokenType.LtEq)) left = new BinaryOpNode(left, BinaryOp.LtEq, ParseAddition());
                else if (Match(TokenType.GtEq)) left = new BinaryOpNode(left, BinaryOp.GtEq, ParseAddition());
                else break;
            }
            return left;
        }

        private ExprNode ParseAddition()
        {
            var left = ParseMultiply();
            while (true)
            {
                if (Match(TokenType.Plus)) left = new BinaryOpNode(left, BinaryOp.Add, ParseMultiply());
                else if (Match(TokenType.Minus)) left = new BinaryOpNode(left, BinaryOp.Sub, ParseMultiply());
                else break;
            }
            return left;
        }

        private ExprNode ParseMultiply()
        {
            var left = ParseUnary();
            while (true)
            {
                if (Match(TokenType.Star)) left = new BinaryOpNode(left, BinaryOp.Mul, ParseUnary());
                else if (Match(TokenType.Slash)) left = new BinaryOpNode(left, BinaryOp.Div, ParseUnary());
                else if (Match(TokenType.Percent)) left = new BinaryOpNode(left, BinaryOp.Mod, ParseUnary());
                else break;
            }
            return left;
        }

        private ExprNode ParseUnary()
        {
            // Try cast: (TypeName) expr
            if (Peek()?.Type == TokenType.LParen)
            {
                var saved = Position;
                Position++;
                if (Peek()?.Type == TokenType.Name)
                {
                    var typeName = Advance().Text;
                    if (Match(TokenType.RParen))
                    {
                        // Check if this looks like a cast (next token is a valid unary start)
                        var next = Peek();
                        if (next != null && (next.Type == TokenType.Name || next.Type == TokenType.LParen
                            || next.Type == TokenType.IntLiteral || next.Type == TokenType.DoubleLiteral
                            || next.Type == TokenType.StringLiteral || next.Type == TokenType.True
                            || next.Type == TokenType.False || next.Type == TokenType.Null))
                        {
                            var operand = ParseUnary();
                            return new CastNode(typeName, operand);
                        }
                    }
                }
                // Not a cast, backtrack
                Position = saved;
            }

            return ParsePostfix();
        }

        private ExprNode ParsePostfix()
        {
            var expr = ParsePrimary();
            while (true)
            {
                if (Peek()?.Type == TokenType.Dot)
                {
                    Position++;
                    var name = Expect(TokenType.Name).Text;
                    if (Match(TokenType.LParen))
                    {
                        var args = ParseArgList();
                        Expect(TokenType.RParen);
                        expr = new MethodCallNode(expr, name, args.ToArray());
                    }
                    else
                    {
                        expr = new MemberAccessNode(expr, name);
                    }
                }
                else if (Match(TokenType.LBracket))
                {
                    var index = ParseExpression();
                    Expect(TokenType.RBracket);
                    expr = new IndexAccessNode(expr, index);
                }
                else break;
            }
            return expr;
        }

        private List<ExprNode> ParseArgList()
        {
            var args = new List<ExprNode>();
            if (Peek()?.Type == TokenType.RParen) return args;
            args.Add(ParseExpression());
            while (Match(TokenType.Comma))
                args.Add(ParseExpression());
            return args;
        }

        private ExprNode ParsePrimary()
        {
            var tok = Peek() ?? throw new FormatException("Unexpected end of expression");

            switch (tok.Type)
            {
                case TokenType.IntLiteral:
                    Advance();
                    return new LiteralNode(tok.Value, tok.Value is long ? LiteralKind.Long : LiteralKind.Int);
                case TokenType.DoubleLiteral:
                    Advance();
                    return new LiteralNode(tok.Value, LiteralKind.Double);
                case TokenType.StringLiteral:
                    Advance();
                    return new LiteralNode(tok.Value, LiteralKind.String);
                case TokenType.True:
                    Advance();
                    return new LiteralNode(true, LiteralKind.Bool);
                case TokenType.False:
                    Advance();
                    return new LiteralNode(false, LiteralKind.Bool);
                case TokenType.Null:
                    Advance();
                    return new LiteralNode(null, LiteralKind.Null);
                case TokenType.Name:
                    Advance();
                    return new NameNode(tok.Text);
                case TokenType.LParen:
                    Advance();
                    var expr = ParseExpression();
                    Expect(TokenType.RParen);
                    return expr;
                default:
                    throw new FormatException($"Unexpected token '{tok.Text}'");
            }
        }
    }

    #endregion
}
