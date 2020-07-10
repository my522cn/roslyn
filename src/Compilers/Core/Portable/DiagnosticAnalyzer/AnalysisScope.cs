﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

#nullable enable

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis.Text;
using Roslyn.Utilities;

namespace Microsoft.CodeAnalysis.Diagnostics
{
    /// <summary>
    /// Scope for analyzer execution.
    /// This scope could either be the entire compilation for all analyzers (command line build) or
    /// could be scoped to a specific tree/span and/or a subset of analyzers (CompilationWithAnalyzers).
    /// </summary>
    internal class AnalysisScope
    {
        private readonly Lazy<ImmutableHashSet<DiagnosticAnalyzer>> _lazyAnalyzersSet;

        public SourceOrNonSourceFile? FilterFileOpt { get; }
        public TextSpan? FilterSpanOpt { get; }

        public ImmutableArray<DiagnosticAnalyzer> Analyzers { get; }

        /// <summary>
        /// Syntax trees on which we need to perform syntax analysis.
        /// </summary>
        public IEnumerable<SyntaxTree> SyntaxTrees { get; }

        /// <summary>
        /// Non-source files on which we need to perform analysis.
        /// </summary>
        public IEnumerable<AdditionalText> NonSourceFiles { get; }

        public bool ConcurrentAnalysis { get; }

        /// <summary>
        /// True if we need to categorize diagnostics into local and non-local diagnostics and track the analyzer reporting each diagnostic.
        /// </summary>
        public bool CategorizeDiagnostics { get; }

        /// <summary>
        /// True if we need to perform only syntax analysis for a single tree or non-source file.
        /// </summary>
        public bool IsSyntaxOnlyTreeAnalysis { get; }

        /// <summary>
        /// True if we need to perform analysis for a single tree or non-source file.
        /// </summary>
        public bool IsTreeAnalysis => FilterFileOpt != null;

        /// <summary>
        /// Flag indicating if this is a partial analysis for the corresponding <see cref="CompilationWithAnalyzers"/>,
        /// i.e. <see cref="IsTreeAnalysis"/> is true and/or <see cref="Analyzers"/> is a subset of <see cref="CompilationWithAnalyzers.Analyzers"/>.
        /// </summary>
        public bool IsPartialAnalysis { get; }

        public AnalysisScope(Compilation compilation, AnalyzerOptions? analyzerOptions, ImmutableArray<DiagnosticAnalyzer> analyzers, bool hasAllAnalyzers, bool concurrentAnalysis, bool categorizeDiagnostics)
            : this(compilation.SyntaxTrees, analyzerOptions?.AdditionalFiles ?? ImmutableArray<AdditionalText>.Empty,
                   analyzers, isPartialAnalysis: !hasAllAnalyzers, filterFile: null, filterSpanOpt: null, isSyntaxOnlyTreeAnalysis: false, concurrentAnalysis: concurrentAnalysis, categorizeDiagnostics: categorizeDiagnostics)
        {
        }

        public AnalysisScope(ImmutableArray<DiagnosticAnalyzer> analyzers, SourceOrNonSourceFile filterFile, TextSpan? filterSpan, bool syntaxAnalysis, bool concurrentAnalysis, bool categorizeDiagnostics)
            : this(filterFile.SourceTree != null ? SpecializedCollections.SingletonEnumerable(filterFile.SourceTree) : SpecializedCollections.EmptyEnumerable<SyntaxTree>(),
                   filterFile.NonSourceFile != null ? SpecializedCollections.SingletonEnumerable(filterFile.NonSourceFile) : SpecializedCollections.EmptyEnumerable<AdditionalText>(),
                   analyzers, isPartialAnalysis: true, filterFile, filterSpan, syntaxAnalysis, concurrentAnalysis, categorizeDiagnostics)
        {
        }

        private AnalysisScope(IEnumerable<SyntaxTree> trees, IEnumerable<AdditionalText> nonSourceFiles, ImmutableArray<DiagnosticAnalyzer> analyzers, bool isPartialAnalysis, SourceOrNonSourceFile? filterFile, TextSpan? filterSpanOpt, bool isSyntaxOnlyTreeAnalysis, bool concurrentAnalysis, bool categorizeDiagnostics)
        {
            Debug.Assert(isPartialAnalysis || FilterFileOpt == null);
            Debug.Assert(isPartialAnalysis || FilterSpanOpt == null);
            Debug.Assert(isPartialAnalysis || !isSyntaxOnlyTreeAnalysis);

            SyntaxTrees = trees;
            NonSourceFiles = nonSourceFiles;
            Analyzers = analyzers;
            IsPartialAnalysis = isPartialAnalysis;
            FilterFileOpt = filterFile;
            FilterSpanOpt = filterSpanOpt;
            IsSyntaxOnlyTreeAnalysis = isSyntaxOnlyTreeAnalysis;
            ConcurrentAnalysis = concurrentAnalysis;
            CategorizeDiagnostics = categorizeDiagnostics;

            _lazyAnalyzersSet = new Lazy<ImmutableHashSet<DiagnosticAnalyzer>>(CreateAnalyzersSet);
        }

        private ImmutableHashSet<DiagnosticAnalyzer> CreateAnalyzersSet() => Analyzers.ToImmutableHashSet();

        public bool Contains(DiagnosticAnalyzer analyzer)
        {
            if (!IsPartialAnalysis)
            {
                Debug.Assert(_lazyAnalyzersSet.Value.Contains(analyzer));
                return true;
            }

            return _lazyAnalyzersSet.Value.Contains(analyzer);
        }

        public AnalysisScope WithAnalyzers(ImmutableArray<DiagnosticAnalyzer> analyzers, bool hasAllAnalyzers)
        {
            var isPartialAnalysis = IsTreeAnalysis || !hasAllAnalyzers;
            return new AnalysisScope(SyntaxTrees, NonSourceFiles, analyzers, isPartialAnalysis, FilterFileOpt, FilterSpanOpt, IsSyntaxOnlyTreeAnalysis, ConcurrentAnalysis, CategorizeDiagnostics);
        }

        public static bool ShouldSkipSymbolAnalysis(SymbolDeclaredCompilationEvent symbolEvent)
        {
            // Skip symbol actions for implicitly declared symbols and non-source symbols.
            return symbolEvent.Symbol.IsImplicitlyDeclared || symbolEvent.DeclaringSyntaxReferences.All(s => s.SyntaxTree == null);
        }

        public static bool ShouldSkipDeclarationAnalysis(ISymbol symbol)
        {
            // Skip syntax actions for implicitly declared symbols, except for implicitly declared global namespace symbols.
            return symbol.IsImplicitlyDeclared &&
                !((symbol.Kind == SymbolKind.Namespace && ((INamespaceSymbol)symbol).IsGlobalNamespace));
        }

        public bool ShouldAnalyze(SyntaxTree tree)
        {
            return FilterFileOpt == null || FilterFileOpt.SourceTree == tree;
        }

        public bool ShouldAnalyze(AdditionalText file)
        {
            return FilterFileOpt == null || FilterFileOpt.NonSourceFile == file;
        }

        public bool ShouldAnalyze(ISymbol symbol)
        {
            if (FilterFileOpt == null)
            {
                return true;
            }

            if (FilterFileOpt.SourceTree == null)
            {
                return false;
            }

            foreach (var location in symbol.Locations)
            {
                if (location.SourceTree != null && FilterFileOpt.SourceTree == location.SourceTree && ShouldInclude(location.SourceSpan))
                {
                    return true;
                }
            }

            return false;
        }

        public bool ShouldAnalyze(SyntaxNode node)
        {
            if (FilterFileOpt == null)
            {
                return true;
            }

            if (FilterFileOpt.SourceTree == null)
            {
                return false;
            }

            return ShouldInclude(node.FullSpan);
        }

        private bool ShouldInclude(TextSpan filterSpan)
        {
            return !FilterSpanOpt.HasValue || FilterSpanOpt.Value.IntersectsWith(filterSpan);
        }

        public bool ContainsSpan(TextSpan filterSpan)
        {
            return !FilterSpanOpt.HasValue || FilterSpanOpt.Value.Contains(filterSpan);
        }

        public bool ShouldInclude(Diagnostic diagnostic)
        {
            if (FilterFileOpt == null)
            {
                return true;
            }

            if (diagnostic.Location.IsInSource)
            {
                if (FilterFileOpt?.SourceTree == null ||
                    diagnostic.Location.SourceTree != FilterFileOpt.SourceTree)
                {
                    return false;
                }
            }
            else if (diagnostic.Location is ExternalFileLocation externalFileLocation)
            {
                if (FilterFileOpt?.NonSourceFile == null ||
                    !PathUtilities.Comparer.Equals(externalFileLocation.FilePath, FilterFileOpt.NonSourceFile.Path))
                {
                    return false;
                }
            }

            return ShouldInclude(diagnostic.Location.SourceSpan);
        }
    }
}
