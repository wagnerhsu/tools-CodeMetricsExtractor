﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using ArchiMetrics.Analysis;
using ArchiMetrics.Common;
using ArchiMetrics.Common.Metrics;
using MetricsExtractor.Custom;
using MetricsExtractor.ReportTemplate;
using Microsoft.CodeAnalysis;

namespace MetricsExtractor
{
    class Program
    {
        private static readonly List<ClassRank> ClassRanks = Enum.GetValues(typeof(ClassRank)).Cast<ClassRank>().ToList();
        private static readonly string ApplicationPath = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);

        static void Main(string[] args)
        {
            if (!args.Any())
            {
                Console.WriteLine("No solution configured");
                return;
            }

            var metricConfiguration = DashedParameterSerializer.Deserialize<MetricConfiguration>(args);

            var runCodeMetrics = RunCodeMetrics(metricConfiguration.Solution, metricConfiguration.IgnoredProjects);
            runCodeMetrics.Wait();
            var ignoredNamespaces = metricConfiguration.IgnoredNamespaces ?? Enumerable.Empty<string>();
            var namespaceMetrics = runCodeMetrics.Result.Where(nm => !ignoredNamespaces.Contains(nm.Name)).ToList();

            System.Diagnostics.Debugger.Launch();
            var types = namespaceMetrics.SelectMany(x => x.TypeMetrics, (nm, t) => new TypeMetricWithNamespace(t).WithNamespace(nm.Name)).Distinct().ToList();

            const int MAX_LINES_OF_CODE_ON_METHOD = 30;

            var metodos = types.SelectMany(x => x.MemberMetrics, (type, member) => new MetodoComTipo { Tipo = type, Metodo = member }).ToList();

            var metodosRuins = GetMetodosRuins(metodos, MAX_LINES_OF_CODE_ON_METHOD);

            var resultadoGeral = CreateEstadoDoProjeto(types, metodosRuins, metodos.Count, namespaceMetrics);

            resultadoGeral.TotalDeClassesPorRank = GetClassesGroupedByRank(types);

            var reportPath = GenerateReport(resultadoGeral, metricConfiguration.SolutionDirectory);

#if DEBUG
            Process.Start(reportPath);
#endif
        }

        private static string GenerateReport(EstadoDoProjeto resultadoGeral, string solutionDirectory)
        {
            var reportDirectory = Path.Combine(solutionDirectory, "CodeMetricsReport");
            var reportPath = Path.Combine(reportDirectory, "CodeMetricsReport.zip");

            var reportTemplateFactory = new ReportTemplateFactory();
            var report = reportTemplateFactory.GetReport(resultadoGeral);
            var list = new[] { "*.css", "*.js" }.SelectMany(ext => Directory.GetFiles(Path.Combine(ApplicationPath, "ReportTemplate"), ext)).ToList();
            

            Directory.CreateDirectory(reportDirectory);
            using (var zipArchive = new ZipArchive(File.OpenWrite(reportPath), ZipArchiveMode.Create))
            {
                foreach (var item in list)
                {
                    var fileName = Path.GetFileName(item);
                    zipArchive.CreateEntryFromFile(item, fileName);
#if DEBUG
                    File.Copy(item, Path.Combine(reportDirectory, fileName), true);
#endif
                }
                var archiveEntry = zipArchive.CreateEntry("Index.html");
                using (var stream = archiveEntry.Open())
                using (var streamWriter = new StreamWriter(stream, Encoding.UTF8))
                    streamWriter.Write(report);
#if DEBUG
                reportPath = Path.Combine(reportDirectory, "Index.html");
                File.WriteAllText(reportPath, report, Encoding.UTF8);
#endif
                zipArchive.Dispose();
            }
            return reportPath;
        }

        private static Dictionary<ClassRank, int> GetClassesGroupedByRank(List<TypeMetricWithNamespace> types)
        {
            var classesGroupedByRank = new Dictionary<ClassRank, int>();
            ClassRanks.ForEach(c => classesGroupedByRank.Add(c, 0));
            var classRankings = CreateClassesRank(types).ToList();
            foreach (var group in classRankings.GroupBy(c => c.Rank))
                classesGroupedByRank[@group.Key] = @group.Count();
            return classesGroupedByRank;
        }

        private static EstadoDoProjeto CreateEstadoDoProjeto(List<TypeMetricWithNamespace> types, List<MetodoRuim> metodosRuins, int totalDeMetodos, IEnumerable<INamespaceMetric> namespaceMetrics)
        {
            var resultadoGeral = new EstadoDoProjeto
            {
                Manutenibilidade = (int)types.Average(x => x.MaintainabilityIndex),
                MediaComplexidadeCiclomatica = types.Average(x => x.CyclomaticComplexity),
                MaiorComplexidadeCiclomatica = types.Max(x => x.CyclomaticComplexity),
                LinhasDeCodigo = types.Sum(x => x.SourceLinesOfCode),
                AcoplamentoAbsoluto = types.Average(x => x.ClassCoupling),
                ProfuDeHeranca = types.Average(x => x.DepthOfInheritance),
                MetodosRuins = metodosRuins,
                TotalDeMetodos = totalDeMetodos,
                TypesLinesOfCode = types.OrderByDescending(x => x.SourceLinesOfCode).ToList()
            };
            return resultadoGeral;
        }

        private static List<MetodoRuim> GetMetodosRuins(List<MetodoComTipo> metodos, int maxLinesOfCodeOnMethod)
        {
            var metodosRuins = metodos
                .Where(x => (x.Metodo.SourceLinesOfCode >= maxLinesOfCodeOnMethod) || (x.Metodo.CyclomaticComplexity >= 10))
                .Select(x => new MetodoRuim
                {
                    ClassName = x.Tipo.Name,
                    NomeMetodo = x.Metodo.Name,
                    Complexidade = x.Metodo.CyclomaticComplexity,
                    Manutenibilidade = x.Metodo.MaintainabilityIndex,
                    QuantidadeDeLinhas = x.Metodo.SourceLinesOfCode,
                })
                .OrderByDescending(x => x.QuantidadeDeLinhas).ThenByDescending(x => x.Complexidade)
                .ToList();
            return metodosRuins;
        }

        private static async Task<IEnumerable<INamespaceMetric>> RunCodeMetrics(string solutionPath, string[] ignoredProjects)
        {
            Console.WriteLine("Loading Solution");
            var solutionProvider = new SolutionProvider();
            var solution = await solutionProvider.Get(solutionPath);
            Console.WriteLine("Solution loaded");

            var projects = solution.Projects.Where(p => !ignoredProjects.Contains(p.Name)).ToList();
            
            Console.WriteLine("Loading metrics, wait it may take a while.");
            List<INamespaceMetric> metrics  = new List<INamespaceMetric>();
            foreach (var project in projects)
            {
                using (new TimerMeasure(string.Format("Loading metrics from project {0}", project.Name), string.Format("{0} metrics loaded", project.Name)))
                {
                    var namespaceMetrics = await CreateTask(project, solution);
                    metrics.AddRange(namespaceMetrics); 
                }
            }
            return metrics;
        }

        private static Task<IEnumerable<INamespaceMetric>> CreateTask(Project p, Solution solution)
        {
            var metricsCalculator = new CodeMetricsCalculator();
            return metricsCalculator.Calculate(p, solution);
        }

        private static IEnumerable<TypeMetricWithNamespace> CreateClassesRank(List<TypeMetricWithNamespace> types)
        {
            return from type in types
                   let maintainabilityIndex = type.MaintainabilityIndex
                   select type.WithRank(ClassRanks.FirstOrDefault(r => (int)r >= maintainabilityIndex));
        }
    }
}