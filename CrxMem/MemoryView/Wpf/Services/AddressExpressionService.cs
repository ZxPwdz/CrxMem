using System.Globalization;

namespace CrxMem.MemoryView.Wpf.Services;

public sealed record AddressExpressionResult(bool Success, ulong Address, string? Error)
{
    public static AddressExpressionResult Resolved(ulong address) => new(true, address, null);
    public static AddressExpressionResult Failed(string error) => new(false, 0, error);
}

public interface IAddressExpressionService
{
    Task<AddressExpressionResult> EvaluateAsync(string expression, CancellationToken cancellationToken);
}

public interface IRegisterValueSource
{
    bool TryGetRegister(string name, out ulong value);
}

public interface ISymbolAddressSource
{
    bool TryGetSymbol(string name, out ulong address);
}

public sealed class EmptyRegisterValueSource : IRegisterValueSource
{
    public static EmptyRegisterValueSource Instance { get; } = new();
    public bool TryGetRegister(string name, out ulong value) { value = 0; return false; }
}

public sealed class EmptySymbolAddressSource : ISymbolAddressSource
{
    public static EmptySymbolAddressSource Instance { get; } = new();
    public bool TryGetSymbol(string name, out ulong address) { address = 0; return false; }
}

public sealed class AddressExpressionService : IAddressExpressionService
{
    private const int MaximumDereferenceDepth = 4;
    private readonly IProcessMemoryService _memory;
    private readonly IModuleService _modules;
    private readonly IRegisterValueSource _registers;
    private readonly ISymbolAddressSource _symbols;

    public AddressExpressionService(
        IProcessMemoryService memory,
        IModuleService modules,
        IRegisterValueSource? registers = null,
        ISymbolAddressSource? symbols = null)
    {
        _memory = memory;
        _modules = modules;
        _registers = registers ?? EmptyRegisterValueSource.Instance;
        _symbols = symbols ?? EmptySymbolAddressSource.Instance;
    }

    public async Task<AddressExpressionResult> EvaluateAsync(
        string expression,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(expression))
            return AddressExpressionResult.Failed("Enter an address expression.");

        try
        {
            var parser = new Parser(expression);
            ExpressionNode root = parser.Parse();
            ulong address = await EvaluateNodeAsync(root, 0, cancellationToken);
            return AddressExpressionResult.Resolved(address);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (ExpressionException ex)
        {
            return AddressExpressionResult.Failed(ex.Message);
        }
        catch (OverflowException)
        {
            return AddressExpressionResult.Failed("The address expression overflowed the target address range.");
        }
    }

    private async Task<ulong> EvaluateNodeAsync(
        ExpressionNode node,
        int dereferenceDepth,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        switch (node)
        {
            case LiteralNode literal:
                return literal.Value;
            case IdentifierNode identifier:
                return ResolveIdentifier(identifier.Name);
            case UnaryNode unary:
            {
                ulong value = await EvaluateNodeAsync(unary.Operand, dereferenceDepth, cancellationToken);
                if (unary.Operator == TokenKind.Plus) return value;
                if (value != 0) throw new ExpressionException("A memory address cannot be negative.");
                return 0;
            }
            case BinaryNode binary:
            {
                ulong left = await EvaluateNodeAsync(binary.Left, dereferenceDepth, cancellationToken);
                ulong right = await EvaluateNodeAsync(binary.Right, dereferenceDepth, cancellationToken);
                return binary.Operator switch
                {
                    TokenKind.Plus => checked(left + right),
                    TokenKind.Minus when left >= right => left - right,
                    TokenKind.Minus => throw new ExpressionException("The address expression produced a negative value."),
                    TokenKind.Star => checked(left * right),
                    TokenKind.Slash when right != 0 => left / right,
                    TokenKind.Slash => throw new ExpressionException("Division by zero is not valid in an address expression."),
                    _ => throw new ExpressionException("Unsupported address operator.")
                };
            }
            case DereferenceNode dereference:
            {
                if (dereferenceDepth >= MaximumDereferenceDepth)
                    throw new ExpressionException($"Pointer dereferences are limited to {MaximumDereferenceDepth} levels.");

                ulong pointerAddress = await EvaluateNodeAsync(
                    dereference.Operand, dereferenceDepth + 1, cancellationToken);
                int pointerSize = _memory.Identity.Is64Bit ? 8 : 4;
                MemoryReadResult result = await _memory.ReadAsync(pointerAddress, pointerSize, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                if (result.Status == MemoryReadStatus.ProcessUnavailable)
                    throw new ExpressionException(result.Error ?? "The selected process is unavailable.");
                if (result.Data.Length < pointerSize)
                    throw new ExpressionException(result.Error ?? $"Unable to read a pointer at 0x{pointerAddress:X}.");
                return pointerSize == 8
                    ? BitConverter.ToUInt64(result.Data, 0)
                    : BitConverter.ToUInt32(result.Data, 0);
            }
            default:
                throw new ExpressionException("Invalid address expression.");
        }
    }

    private ulong ResolveIdentifier(string name)
    {
        ModuleDescriptor? module = _modules.FindByName(name);
        if (module != null) return module.BaseAddress;
        if (_registers.TryGetRegister(name, out ulong register)) return register;
        if (_symbols.TryGetSymbol(name, out ulong symbol)) return symbol;
        throw new ExpressionException($"Unknown module, register, or symbol '{name}'.");
    }

    private enum TokenKind { Number, Identifier, Plus, Minus, Star, Slash, LeftParen, RightParen, LeftBracket, RightBracket, End }
    private readonly record struct Token(TokenKind Kind, string Text, int Position);
    private abstract record ExpressionNode;
    private sealed record LiteralNode(ulong Value) : ExpressionNode;
    private sealed record IdentifierNode(string Name) : ExpressionNode;
    private sealed record UnaryNode(TokenKind Operator, ExpressionNode Operand) : ExpressionNode;
    private sealed record BinaryNode(ExpressionNode Left, TokenKind Operator, ExpressionNode Right) : ExpressionNode;
    private sealed record DereferenceNode(ExpressionNode Operand) : ExpressionNode;

    private sealed class Parser
    {
        private readonly string _text;
        private int _position;
        private Token _current;

        public Parser(string text)
        {
            _text = text;
            _current = ReadToken();
        }

        public ExpressionNode Parse()
        {
            ExpressionNode result = ParseExpression();
            if (_current.Kind != TokenKind.End)
                throw Error($"Unexpected token '{_current.Text}'.", _current.Position);
            return result;
        }

        private ExpressionNode ParseExpression()
        {
            ExpressionNode left = ParseTerm();
            while (_current.Kind is TokenKind.Plus or TokenKind.Minus)
            {
                TokenKind operation = _current.Kind;
                Advance();
                left = new BinaryNode(left, operation, ParseTerm());
            }
            return left;
        }

        private ExpressionNode ParseTerm()
        {
            ExpressionNode left = ParseUnary();
            while (_current.Kind is TokenKind.Star or TokenKind.Slash)
            {
                TokenKind operation = _current.Kind;
                Advance();
                left = new BinaryNode(left, operation, ParseUnary());
            }
            return left;
        }

        private ExpressionNode ParseUnary()
        {
            if (_current.Kind is TokenKind.Plus or TokenKind.Minus)
            {
                TokenKind operation = _current.Kind;
                Advance();
                return new UnaryNode(operation, ParseUnary());
            }
            return ParsePrimary();
        }

        private ExpressionNode ParsePrimary()
        {
            Token token = _current;
            switch (token.Kind)
            {
                case TokenKind.Number:
                    Advance();
                    string digits = token.Text.Replace("`", string.Empty);
                    if (digits.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) digits = digits[2..];
                    if (!ulong.TryParse(digits, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong value))
                        throw Error($"'{token.Text}' is not a valid hexadecimal address.", token.Position);
                    return new LiteralNode(value);
                case TokenKind.Identifier:
                    Advance();
                    return new IdentifierNode(token.Text);
                case TokenKind.LeftParen:
                    Advance();
                    ExpressionNode grouped = ParseExpression();
                    Expect(TokenKind.RightParen, "Missing ')' in address expression.");
                    return grouped;
                case TokenKind.LeftBracket:
                    Advance();
                    ExpressionNode pointer = ParseExpression();
                    Expect(TokenKind.RightBracket, "Missing ']' in pointer expression.");
                    return new DereferenceNode(pointer);
                default:
                    throw Error($"Expected an address, module, register, symbol, or grouped expression at position {token.Position}.", token.Position);
            }
        }

        private void Expect(TokenKind kind, string message)
        {
            if (_current.Kind != kind) throw Error(message, _current.Position);
            Advance();
        }

        private void Advance() => _current = ReadToken();

        private Token ReadToken()
        {
            while (_position < _text.Length && char.IsWhiteSpace(_text[_position])) _position++;
            if (_position >= _text.Length) return new Token(TokenKind.End, string.Empty, _position);

            int start = _position;
            char value = _text[_position++];
            TokenKind? punctuation = value switch
            {
                '+' => TokenKind.Plus, '-' => TokenKind.Minus, '*' => TokenKind.Star, '/' => TokenKind.Slash,
                '(' => TokenKind.LeftParen, ')' => TokenKind.RightParen,
                '[' => TokenKind.LeftBracket, ']' => TokenKind.RightBracket,
                _ => null
            };
            if (punctuation.HasValue) return new Token(punctuation.Value, value.ToString(), start);

            if (char.IsDigit(value))
            {
                while (_position < _text.Length &&
                       (Uri.IsHexDigit(_text[_position]) || _text[_position] is 'x' or 'X' or '`'))
                    _position++;
                return new Token(TokenKind.Number, _text[start.._position], start);
            }

            if (char.IsLetter(value) || value is '_' or '.' or '!')
            {
                while (_position < _text.Length &&
                       (char.IsLetterOrDigit(_text[_position]) || _text[_position] is '_' or '.' or '!' or '$'))
                    _position++;
                return new Token(TokenKind.Identifier, _text[start.._position], start);
            }

            throw Error($"Unexpected character '{value}' at position {start}.", start);
        }

        private static ExpressionException Error(string message, int position) =>
            new(message.Contains("position", StringComparison.OrdinalIgnoreCase) ? message : $"{message} Position {position}.");
    }

    private sealed class ExpressionException(string message) : Exception(message);
}
