// Generated from CaDiCaL options.hpp (do not edit by hand).
namespace LeanSharp.Runtime.Cadical;

public sealed partial class Options
{
    public static OptionInfo[] MakeTable(int reportdefault) => new OptionInfo[] {
        new OptionInfo("arena", 1, 0, 1, 0, false, true, "allocate clauses in arena", "1", "0", "1", 0),
        new OptionInfo("arenacompact", 1, 0, 1, 0, false, true, "keep clauses compact", "1", "0", "1", 1),
        new OptionInfo("arenasort", 1, 0, 1, 0, false, true, "sort clauses in arena", "1", "0", "1", 2),
        new OptionInfo("arenatype", 3, 1, 3, 0, false, true, "1=clause, 2=var, 3=queue", "3", "1", "3", 3),
        new OptionInfo("binary", 1, 0, 1, 0, false, true, "use binary proof format", "1", "0", "1", 4),
        new OptionInfo("block", 0, 0, 1, 0, true, true, "blocked clause elimination", "0", "0", "1", 5),
        new OptionInfo("blockmaxclslim", 100000, 1, 2000000000, 2, false, true, "maximum clause size", "1e5", "1", "2e9", 6),
        new OptionInfo("blockminclslim", 2, 2, 2000000000, 0, false, true, "minimum clause size", "2", "2", "2e9", 7),
        new OptionInfo("blockocclim", 100, 1, 2000000000, 2, false, true, "occurrence limit", "1e2", "1", "2e9", 8),
        new OptionInfo("bump", 1, 0, 1, 0, false, true, "bump variables", "1", "0", "1", 9),
        new OptionInfo("bumpreason", 1, 0, 1, 0, false, true, "bump reason literals too", "1", "0", "1", 10),
        new OptionInfo("bumpreasondepth", 1, 1, 3, 0, false, true, "bump reason depth", "1", "1", "3", 11),
        new OptionInfo("check", 0, 0, 1, 0, false, false, "enable internal checking", "0", "0", "1", 12),
        new OptionInfo("checkassumptions", 1, 0, 1, 0, false, false, "check assumptions satisfied", "1", "0", "1", 13),
        new OptionInfo("checkconstraint", 1, 0, 1, 0, false, false, "check constraint satisfied", "1", "0", "1", 14),
        new OptionInfo("checkfailed", 1, 0, 1, 0, false, false, "check failed literals form core", "1", "0", "1", 15),
        new OptionInfo("checkfrozen", 0, 0, 1, 0, false, false, "check all frozen semantics", "0", "0", "1", 16),
        new OptionInfo("checkproof", 3, 0, 3, 0, false, false, "1=drat, 2=lrat, 3=both", "3", "0", "3", 17),
        new OptionInfo("checkwitness", 1, 0, 1, 0, false, false, "check witness internally", "1", "0", "1", 18),
        new OptionInfo("chrono", 1, 0, 2, 0, false, true, "chronological backtracking", "1", "0", "2", 19),
        new OptionInfo("chronoalways", 0, 0, 1, 0, false, true, "force always chronological", "0", "0", "1", 20),
        new OptionInfo("chronolevelim", 100, 0, 2000000000, 0, false, true, "chronological level limit", "1e2", "0", "2e9", 21),
        new OptionInfo("chronoreusetrail", 1, 0, 1, 0, false, true, "reuse trail chronologically", "1", "0", "1", 22),
        new OptionInfo("compact", 1, 0, 1, 0, true, true, "compact internal variables", "1", "0", "1", 23),
        new OptionInfo("compactint", 2000, 1, 2000000000, 0, false, true, "compacting interval", "2e3", "1", "2e9", 24),
        new OptionInfo("compactlim", 100, 0, 1000, 0, false, true, "inactive limit per mille", "1e2", "0", "1e3", 25),
        new OptionInfo("compactmin", 100, 1, 2000000000, 0, false, true, "minimum inactive limit", "1e2", "1", "2e9", 26),
        new OptionInfo("condition", 0, 0, 1, 0, false, true, "globally blocked clause elim", "0", "0", "1", 27),
        new OptionInfo("conditionint", 10000, 1, 2000000000, 0, false, true, "initial conflict interval", "1e4", "1", "2e9", 28),
        new OptionInfo("conditionmaxeff", 10000000, 0, 2000000000, 1, false, true, "maximum condition efficiency", "1e7", "0", "2e9", 29),
        new OptionInfo("conditionmaxrat", 100, 1, 2000000000, 1, false, true, "maximum clause variable ratio", "100", "1", "2e9", 30),
        new OptionInfo("conditionmineff", 1000000, 0, 2000000000, 1, false, true, "minimum condition efficiency", "1e6", "0", "2e9", 31),
        new OptionInfo("conditionreleff", 100, 1, 100000, 0, false, true, "relative efficiency per mille", "100", "1", "1e5", 32),
        new OptionInfo("cover", 0, 0, 1, 0, true, true, "covered clause elimination", "0", "0", "1", 33),
        new OptionInfo("covermaxclslim", 100000, 1, 2000000000, 2, false, true, "maximum clause size", "1e5", "1", "2e9", 34),
        new OptionInfo("covermaxeff", 100000000, 0, 2000000000, 1, false, true, "maximum cover efficiency", "1e8", "0", "2e9", 35),
        new OptionInfo("coverminclslim", 2, 2, 2000000000, 0, false, true, "minimum clause size", "2", "2", "2e9", 36),
        new OptionInfo("covermineff", 1000000, 0, 2000000000, 1, false, true, "minimum cover efficiency", "1e6", "0", "2e9", 37),
        new OptionInfo("coverreleff", 4, 1, 100000, 1, false, true, "relative efficiency per mille", "4", "1", "1e5", 38),
        new OptionInfo("decompose", 1, 0, 1, 0, true, true, "decompose BIG in SCCs and ELS", "1", "0", "1", 39),
        new OptionInfo("decomposerounds", 2, 1, 16, 1, false, true, "number of decompose rounds", "2", "1", "16", 40),
        new OptionInfo("deduplicate", 1, 0, 1, 0, true, true, "remove duplicated binaries", "1", "0", "1", 41),
        new OptionInfo("eagersubsume", 1, 0, 1, 0, false, true, "subsume recently learned", "1", "0", "1", 42),
        new OptionInfo("eagersubsumelim", 20, 1, 1000, 0, false, true, "limit on subsumed candidates", "20", "1", "1e3", 43),
        new OptionInfo("elim", 1, 0, 1, 0, true, true, "bounded variable elimination", "1", "0", "1", 44),
        new OptionInfo("elimands", 1, 0, 1, 0, false, true, "find AND gates", "1", "0", "1", 45),
        new OptionInfo("elimaxeff", 2000000000, 0, 2000000000, 1, false, true, "maximum elimination efficiency", "2e9", "0", "2e9", 46),
        new OptionInfo("elimbackward", 1, 0, 1, 0, false, true, "eager backward subsumption", "1", "0", "1", 47),
        new OptionInfo("elimboundmax", 16, -1, 2000000, 1, false, true, "maximum elimination bound", "16", "-1", "2e6", 48),
        new OptionInfo("elimboundmin", 0, -1, 2000000, 0, false, true, "minimum elimination bound", "0", "-1", "2e6", 49),
        new OptionInfo("elimclslim", 100, 2, 2000000000, 2, false, true, "resolvent size limit", "1e2", "2", "2e9", 50),
        new OptionInfo("elimequivs", 1, 0, 1, 0, false, true, "find equivalence gates", "1", "0", "1", 51),
        new OptionInfo("elimineff", 10000000, 0, 2000000000, 1, false, true, "minimum elimination efficiency", "1e7", "0", "2e9", 52),
        new OptionInfo("elimint", 2000, 1, 2000000000, 0, false, true, "elimination interval", "2e3", "1", "2e9", 53),
        new OptionInfo("elimites", 1, 0, 1, 0, false, true, "find if-then-else gates", "1", "0", "1", 54),
        new OptionInfo("elimlimited", 1, 0, 1, 0, false, true, "limit resolutions", "1", "0", "1", 55),
        new OptionInfo("elimocclim", 100, 0, 2000000000, 2, false, true, "occurrence limit", "1e2", "0", "2e9", 56),
        new OptionInfo("elimprod", 1, 0, 10000, 0, false, true, "elim score product weight", "1", "0", "1e4", 57),
        new OptionInfo("elimreleff", 1000, 1, 100000, 1, false, true, "relative efficiency per mille", "1e3", "1", "1e5", 58),
        new OptionInfo("elimrounds", 2, 1, 512, 1, false, true, "usual number of rounds", "2", "1", "512", 59),
        new OptionInfo("elimsubst", 1, 0, 1, 0, false, true, "elimination by substitution", "1", "0", "1", 60),
        new OptionInfo("elimsum", 1, 0, 10000, 0, false, true, "elimination score sum weight", "1", "0", "1e4", 61),
        new OptionInfo("elimxorlim", 5, 2, 27, 1, false, true, "maximum XOR size", "5", "2", "27", 62),
        new OptionInfo("elimxors", 1, 0, 1, 0, false, true, "find XOR gates", "1", "0", "1", 63),
        new OptionInfo("emagluefast", 33, 1, 2000000000, 0, false, true, "window fast glue", "33", "1", "2e9", 64),
        new OptionInfo("emaglueslow", 100000, 1, 2000000000, 0, false, true, "window slow glue", "1e5", "1", "2e9", 65),
        new OptionInfo("emajump", 100000, 1, 2000000000, 0, false, true, "window back-jump level", "1e5", "1", "2e9", 66),
        new OptionInfo("emalevel", 100000, 1, 2000000000, 0, false, true, "window back-track level", "1e5", "1", "2e9", 67),
        new OptionInfo("emasize", 100000, 1, 2000000000, 0, false, true, "window learned clause size", "1e5", "1", "2e9", 68),
        new OptionInfo("ematrailfast", 100, 1, 2000000000, 0, false, true, "window fast trail", "1e2", "1", "2e9", 69),
        new OptionInfo("ematrailslow", 100000, 1, 2000000000, 0, false, true, "window slow trail", "1e5", "1", "2e9", 70),
        new OptionInfo("exteagerreasons", 1, 0, 1, 0, false, true, "eagerly ask for all reasons (0: only when needed)", "1", "0", "1", 71),
        new OptionInfo("exteagerrecalc", 1, 0, 1, 0, false, true, "after eagerly asking for reasons recalculate all levels (0: trust the external tool)", "1", "0", "1", 72),
        new OptionInfo("externallrat", 0, 0, 1, 0, false, true, "external lrat", "0", "0", "1", 73),
        new OptionInfo("flush", 0, 0, 1, 0, false, true, "flush redundant clauses", "0", "0", "1", 74),
        new OptionInfo("flushfactor", 3, 1, 1000, 0, false, true, "interval increase", "3", "1", "1e3", 75),
        new OptionInfo("flushint", 100000, 1, 2000000000, 0, false, true, "initial limit", "1e5", "1", "2e9", 76),
        new OptionInfo("forcephase", 0, 0, 1, 0, false, true, "always use initial phase", "0", "0", "1", 77),
        new OptionInfo("frat", 0, 0, 2, 0, false, true, "1=frat(lrat), 2=frat(drat)", "0", "0", "2", 78),
        new OptionInfo("idrup", 0, 0, 1, 0, false, true, "incremental proof format", "0", "0", "1", 79),
        new OptionInfo("ilb", 0, 0, 1, 0, false, true, "ILB (incremental lazy backtrack)", "0", "0", "1", 80),
        new OptionInfo("ilbassumptions", 0, 0, 1, 0, false, true, "trail reuse for assumptions (ILB-like)", "0", "0", "1", 81),
        new OptionInfo("inprocessing", 1, 0, 1, 0, false, true, "enable inprocessing", "1", "0", "1", 82),
        new OptionInfo("instantiate", 0, 0, 1, 0, true, true, "variable instantiation", "0", "0", "1", 83),
        new OptionInfo("instantiateclslim", 3, 2, 2000000000, 0, false, true, "minimum clause size", "3", "2", "2e9", 84),
        new OptionInfo("instantiateocclim", 1, 1, 2000000000, 2, false, true, "maximum occurrence limit", "1", "1", "2e9", 85),
        new OptionInfo("instantiateonce", 1, 0, 1, 0, false, true, "instantiate each clause once", "1", "0", "1", 86),
        new OptionInfo("lidrup", 0, 0, 1, 0, false, true, "linear incremental proof format", "0", "0", "1", 87),
        new OptionInfo("lrat", 0, 0, 1, 0, false, true, "use LRAT proof format", "0", "0", "1", 88),
        new OptionInfo("lucky", 1, 0, 1, 0, false, true, "search for lucky phases", "1", "0", "1", 89),
        new OptionInfo("minimize", 1, 0, 1, 0, false, true, "minimize learned clauses", "1", "0", "1", 90),
        new OptionInfo("minimizedepth", 1000, 0, 1000, 0, false, true, "minimization depth", "1e3", "0", "1e3", 91),
        new OptionInfo("otfs", 1, 0, 1, 0, false, true, "on-the-fly self subsumption", "1", "0", "1", 92),
        new OptionInfo("phase", 1, 0, 1, 0, false, true, "initial phase", "1", "0", "1", 93),
        new OptionInfo("probe", 1, 0, 1, 0, true, true, "failed literal probing", "1", "0", "1", 94),
        new OptionInfo("probehbr", 1, 0, 1, 0, false, true, "learn hyper binary clauses", "1", "0", "1", 95),
        new OptionInfo("probeint", 5000, 1, 2000000000, 0, false, true, "probing interval", "5e3", "1", "2e9", 96),
        new OptionInfo("probemaxeff", 100000000, 0, 2000000000, 1, false, true, "maximum probing efficiency", "1e8", "0", "2e9", 97),
        new OptionInfo("probemineff", 1000000, 0, 2000000000, 1, false, true, "minimum probing efficiency", "1e6", "0", "2e9", 98),
        new OptionInfo("probereleff", 20, 1, 100000, 1, false, true, "relative efficiency per mille", "20", "1", "1e5", 99),
        new OptionInfo("proberounds", 1, 1, 16, 1, false, true, "probing rounds", "1", "1", "16", 100),
        new OptionInfo("profile", 2, 0, 4, 0, false, false, "profiling level", "2", "0", "4", 101),
        new OptionInfo("quiet", 0, 0, 1, 0, false, false, "disable all messages", "0", "0", "1", 102),
        new OptionInfo("radixsortlim", 32, 0, 2000000000, 0, false, true, "radix sort limit", "32", "0", "2e9", 103),
        new OptionInfo("realtime", 0, 0, 1, 0, false, false, "real instead of process time", "0", "0", "1", 104),
        new OptionInfo("reduce", 1, 0, 1, 0, false, true, "reduce useless clauses", "1", "0", "1", 105),
        new OptionInfo("reduceint", 300, 10, 1000000, 0, false, true, "reduce interval", "300", "10", "1e6", 106),
        new OptionInfo("reducetarget", 75, 10, 100, 0, false, true, "reduce fraction in percent", "75", "10", "1e2", 107),
        new OptionInfo("reducetier1glue", 2, 1, 2000000000, 0, false, true, "glue of kept learned clauses", "2", "1", "2e9", 108),
        new OptionInfo("reducetier2glue", 6, 1, 2000000000, 0, false, true, "glue of tier two clauses", "6", "1", "2e9", 109),
        new OptionInfo("reluctant", 1024, 0, 2000000000, 0, false, true, "reluctant doubling period", "1024", "0", "2e9", 110),
        new OptionInfo("reluctantmax", 1048576, 0, 2000000000, 0, false, true, "reluctant doubling period", "1048576", "0", "2e9", 111),
        new OptionInfo("rephase", 1, 0, 1, 0, false, true, "enable resetting phase", "1", "0", "1", 112),
        new OptionInfo("rephaseint", 1000, 1, 2000000000, 0, false, true, "rephase interval", "1e3", "1", "2e9", 113),
        new OptionInfo("report", reportdefault, 0, 1, 0, false, true, "enable reporting", "reportdefault", "0", "1", 114),
        new OptionInfo("reportall", 0, 0, 1, 0, false, true, "report even if not successful", "0", "0", "1", 115),
        new OptionInfo("reportsolve", 0, 0, 1, 0, false, true, "use solving not process time", "0", "0", "1", 116),
        new OptionInfo("restart", 1, 0, 1, 0, false, true, "enable restarts", "1", "0", "1", 117),
        new OptionInfo("restartint", 2, 1, 2000000000, 0, false, true, "restart interval", "2", "1", "2e9", 118),
        new OptionInfo("restartmargin", 10, 0, 100, 0, false, true, "slow fast margin in percent", "10", "0", "1e2", 119),
        new OptionInfo("restartreusetrail", 1, 0, 1, 0, false, true, "enable trail reuse", "1", "0", "1", 120),
        new OptionInfo("restoreall", 0, 0, 2, 0, false, true, "restore all clauses (2=really)", "0", "0", "2", 121),
        new OptionInfo("restoreflush", 0, 0, 1, 0, false, true, "remove satisfied clauses", "0", "0", "1", 122),
        new OptionInfo("reverse", 0, 0, 1, 0, false, true, "reverse variable ordering", "0", "0", "1", 123),
        new OptionInfo("score", 1, 0, 1, 0, false, true, "use EVSIDS scores", "1", "0", "1", 124),
        new OptionInfo("scorefactor", 950, 500, 1000, 0, false, true, "score factor per mille", "950", "500", "1e3", 125),
        new OptionInfo("seed", 0, 0, 2000000000, 0, false, true, "random seed", "0", "0", "2e9", 126),
        new OptionInfo("shrink", 3, 0, 3, 0, false, true, "shrink conflict clause (1=only with binary, 2=minimize when pulling, 3=full)", "3", "0", "3", 127),
        new OptionInfo("shrinkreap", 1, 0, 1, 0, false, true, "use a reap for shrinking", "1", "0", "1", 128),
        new OptionInfo("shuffle", 0, 0, 1, 0, false, true, "shuffle variables", "0", "0", "1", 129),
        new OptionInfo("shufflequeue", 1, 0, 1, 0, false, true, "shuffle variable queue", "1", "0", "1", 130),
        new OptionInfo("shufflerandom", 0, 0, 1, 0, false, true, "not reverse but random", "0", "0", "1", 131),
        new OptionInfo("shufflescores", 1, 0, 1, 0, false, true, "shuffle variable scores", "1", "0", "1", 132),
        new OptionInfo("stabilize", 1, 0, 1, 0, false, true, "enable stabilizing phases", "1", "0", "1", 133),
        new OptionInfo("stabilizefactor", 200, 101, 2000000000, 0, false, true, "phase increase in percent", "200", "101", "2e9", 134),
        new OptionInfo("stabilizeint", 1000, 1, 2000000000, 0, false, true, "stabilizing interval", "1e3", "1", "2e9", 135),
        new OptionInfo("stabilizemaxint", 2000000000, 1, 2000000000, 0, false, true, "maximum stabilizing phase", "2e9", "1", "2e9", 136),
        new OptionInfo("stabilizeonly", 0, 0, 1, 0, false, true, "only stabilizing phases", "0", "0", "1", 137),
        new OptionInfo("stats", 0, 0, 1, 0, false, true, "print all statistics at the end of the run", "0", "0", "1", 138),
        new OptionInfo("subsume", 1, 0, 1, 0, true, true, "enable clause subsumption", "1", "0", "1", 139),
        new OptionInfo("subsumebinlim", 10000, 0, 2000000000, 1, false, true, "watch list length limit", "1e4", "0", "2e9", 140),
        new OptionInfo("subsumeclslim", 100, 0, 2000000000, 2, false, true, "clause length limit", "1e2", "0", "2e9", 141),
        new OptionInfo("subsumeint", 10000, 1, 2000000000, 0, false, true, "subsume interval", "1e4", "1", "2e9", 142),
        new OptionInfo("subsumelimited", 1, 0, 1, 0, false, true, "limit subsumption checks", "1", "0", "1", 143),
        new OptionInfo("subsumemaxeff", 100000000, 0, 2000000000, 1, false, true, "maximum subsuming efficiency", "1e8", "0", "2e9", 144),
        new OptionInfo("subsumemineff", 1000000, 0, 2000000000, 1, false, true, "minimum subsuming efficiency", "1e6", "0", "2e9", 145),
        new OptionInfo("subsumeocclim", 100, 0, 2000000000, 1, false, true, "watch list length limit", "1e2", "0", "2e9", 146),
        new OptionInfo("subsumereleff", 1000, 1, 100000, 1, false, true, "relative efficiency per mille", "1e3", "1", "1e5", 147),
        new OptionInfo("subsumestr", 1, 0, 1, 0, false, true, "strengthen during subsume", "1", "0", "1", 148),
        new OptionInfo("target", 1, 0, 2, 0, false, true, "target phases (1=stable only)", "1", "0", "2", 149),
        new OptionInfo("terminateint", 10, 0, 10000, 0, false, true, "termination check interval", "10", "0", "1e4", 150),
        new OptionInfo("ternary", 1, 0, 1, 0, true, true, "hyper ternary resolution", "1", "0", "1", 151),
        new OptionInfo("ternarymaxadd", 1000, 0, 10000, 1, false, true, "max clauses added in percent", "1e3", "0", "1e4", 152),
        new OptionInfo("ternarymaxeff", 100000000, 0, 2000000000, 1, false, true, "ternary maximum efficiency", "1e8", "0", "2e9", 153),
        new OptionInfo("ternarymineff", 1000000, 1, 2000000000, 1, false, true, "minimum ternary efficiency", "1e6", "1", "2e9", 154),
        new OptionInfo("ternaryocclim", 100, 1, 2000000000, 2, false, true, "ternary occurrence limit", "1e2", "1", "2e9", 155),
        new OptionInfo("ternaryreleff", 10, 1, 100000, 1, false, true, "relative efficiency per mille", "10", "1", "1e5", 156),
        new OptionInfo("ternaryrounds", 2, 1, 16, 1, false, true, "maximum ternary rounds", "2", "1", "16", 157),
        new OptionInfo("transred", 1, 0, 1, 0, true, true, "transitive reduction of BIG", "1", "0", "1", 158),
        new OptionInfo("transredmaxeff", 100000000, 0, 2000000000, 1, false, true, "maximum efficiency", "1e8", "0", "2e9", 159),
        new OptionInfo("transredmineff", 1000000, 0, 2000000000, 1, false, true, "minimum efficiency", "1e6", "0", "2e9", 160),
        new OptionInfo("transredreleff", 100, 1, 100000, 1, false, true, "relative efficiency per mille", "1e2", "1", "1e5", 161),
        new OptionInfo("verbose", 0, 0, 3, 0, false, false, "more verbose messages", "0", "0", "3", 162),
        new OptionInfo("veripb", 0, 0, 4, 0, false, true, "odd=checkdeletions, > 2=drat", "0", "0", "4", 163),
        new OptionInfo("vivify", 1, 0, 1, 0, true, true, "vivification", "1", "0", "1", 164),
        new OptionInfo("vivifyinst", 1, 0, 1, 0, false, true, "instantiate last literal when vivify", "1", "0", "1", 165),
        new OptionInfo("vivifymaxeff", 20000000, 0, 2000000000, 1, false, true, "maximum efficiency", "2e7", "0", "2e9", 166),
        new OptionInfo("vivifymineff", 20000, 0, 2000000000, 1, false, true, "minimum efficiency", "2e4", "0", "2e9", 167),
        new OptionInfo("vivifyonce", 0, 0, 2, 0, false, true, "vivify once: 1=red, 2=red+irr", "0", "0", "2", 168),
        new OptionInfo("vivifyredeff", 75, 0, 1000, 1, false, true, "redundant efficiency per mille", "75", "0", "1e3", 169),
        new OptionInfo("vivifyreleff", 20, 1, 100000, 1, false, true, "relative efficiency per mille", "20", "1", "1e5", 170),
        new OptionInfo("walk", 1, 0, 1, 0, false, true, "enable random walks", "1", "0", "1", 171),
        new OptionInfo("walkmaxeff", 10000000, 0, 2000000000, 1, false, true, "maximum efficiency", "1e7", "0", "2e9", 172),
        new OptionInfo("walkmineff", 100000, 0, 10000000, 1, false, true, "minimum efficiency", "1e5", "0", "1e7", 173),
        new OptionInfo("walknonstable", 1, 0, 1, 0, false, true, "walk in non-stabilizing phase", "1", "0", "1", 174),
        new OptionInfo("walkredundant", 0, 0, 1, 0, false, true, "walk redundant clauses too", "0", "0", "1", 175),
        new OptionInfo("walkreleff", 20, 1, 100000, 1, false, true, "relative efficiency per mille", "20", "1", "1e5", 176),
    };

    public int arena, arenacompact, arenasort, arenatype, binary, block, blockmaxclslim, blockminclslim, blockocclim, bump, bumpreason, bumpreasondepth, check, checkassumptions, checkconstraint, checkfailed, checkfrozen, checkproof, checkwitness, chrono, chronoalways, chronolevelim, chronoreusetrail, compact, compactint, compactlim, compactmin, condition, conditionint, conditionmaxeff, conditionmaxrat, conditionmineff, conditionreleff, cover, covermaxclslim, covermaxeff, coverminclslim, covermineff, coverreleff, decompose, decomposerounds, deduplicate, eagersubsume, eagersubsumelim, elim, elimands, elimaxeff, elimbackward, elimboundmax, elimboundmin, elimclslim, elimequivs, elimineff, elimint, elimites, elimlimited, elimocclim, elimprod, elimreleff, elimrounds, elimsubst, elimsum, elimxorlim, elimxors, emagluefast, emaglueslow, emajump, emalevel, emasize, ematrailfast, ematrailslow, exteagerreasons, exteagerrecalc, externallrat, flush, flushfactor, flushint, forcephase, frat, idrup, ilb, ilbassumptions, inprocessing, instantiate, instantiateclslim, instantiateocclim, instantiateonce, lidrup, lrat, lucky, minimize, minimizedepth, otfs, phase, probe, probehbr, probeint, probemaxeff, probemineff, probereleff, proberounds, profile, quiet, radixsortlim, realtime, reduce, reduceint, reducetarget, reducetier1glue, reducetier2glue, reluctant, reluctantmax, rephase, rephaseint, report, reportall, reportsolve, restart, restartint, restartmargin, restartreusetrail, restoreall, restoreflush, reverse, score, scorefactor, seed, shrink, shrinkreap, shuffle, shufflequeue, shufflerandom, shufflescores, stabilize, stabilizefactor, stabilizeint, stabilizemaxint, stabilizeonly, stats, subsume, subsumebinlim, subsumeclslim, subsumeint, subsumelimited, subsumemaxeff, subsumemineff, subsumeocclim, subsumereleff, subsumestr, target, terminateint, ternary, ternarymaxadd, ternarymaxeff, ternarymineff, ternaryocclim, ternaryreleff, ternaryrounds, transred, transredmaxeff, transredmineff, transredreleff, verbose, veripb, vivify, vivifyinst, vivifymaxeff, vivifymineff, vivifyonce, vivifyredeff, vivifyreleff, walk, walkmaxeff, walkmineff, walknonstable, walkredundant, walkreleff;

    public ref int val(int idx)
    {
        switch (idx)
        {
            case 0: return ref arena;
            case 1: return ref arenacompact;
            case 2: return ref arenasort;
            case 3: return ref arenatype;
            case 4: return ref binary;
            case 5: return ref block;
            case 6: return ref blockmaxclslim;
            case 7: return ref blockminclslim;
            case 8: return ref blockocclim;
            case 9: return ref bump;
            case 10: return ref bumpreason;
            case 11: return ref bumpreasondepth;
            case 12: return ref check;
            case 13: return ref checkassumptions;
            case 14: return ref checkconstraint;
            case 15: return ref checkfailed;
            case 16: return ref checkfrozen;
            case 17: return ref checkproof;
            case 18: return ref checkwitness;
            case 19: return ref chrono;
            case 20: return ref chronoalways;
            case 21: return ref chronolevelim;
            case 22: return ref chronoreusetrail;
            case 23: return ref compact;
            case 24: return ref compactint;
            case 25: return ref compactlim;
            case 26: return ref compactmin;
            case 27: return ref condition;
            case 28: return ref conditionint;
            case 29: return ref conditionmaxeff;
            case 30: return ref conditionmaxrat;
            case 31: return ref conditionmineff;
            case 32: return ref conditionreleff;
            case 33: return ref cover;
            case 34: return ref covermaxclslim;
            case 35: return ref covermaxeff;
            case 36: return ref coverminclslim;
            case 37: return ref covermineff;
            case 38: return ref coverreleff;
            case 39: return ref decompose;
            case 40: return ref decomposerounds;
            case 41: return ref deduplicate;
            case 42: return ref eagersubsume;
            case 43: return ref eagersubsumelim;
            case 44: return ref elim;
            case 45: return ref elimands;
            case 46: return ref elimaxeff;
            case 47: return ref elimbackward;
            case 48: return ref elimboundmax;
            case 49: return ref elimboundmin;
            case 50: return ref elimclslim;
            case 51: return ref elimequivs;
            case 52: return ref elimineff;
            case 53: return ref elimint;
            case 54: return ref elimites;
            case 55: return ref elimlimited;
            case 56: return ref elimocclim;
            case 57: return ref elimprod;
            case 58: return ref elimreleff;
            case 59: return ref elimrounds;
            case 60: return ref elimsubst;
            case 61: return ref elimsum;
            case 62: return ref elimxorlim;
            case 63: return ref elimxors;
            case 64: return ref emagluefast;
            case 65: return ref emaglueslow;
            case 66: return ref emajump;
            case 67: return ref emalevel;
            case 68: return ref emasize;
            case 69: return ref ematrailfast;
            case 70: return ref ematrailslow;
            case 71: return ref exteagerreasons;
            case 72: return ref exteagerrecalc;
            case 73: return ref externallrat;
            case 74: return ref flush;
            case 75: return ref flushfactor;
            case 76: return ref flushint;
            case 77: return ref forcephase;
            case 78: return ref frat;
            case 79: return ref idrup;
            case 80: return ref ilb;
            case 81: return ref ilbassumptions;
            case 82: return ref inprocessing;
            case 83: return ref instantiate;
            case 84: return ref instantiateclslim;
            case 85: return ref instantiateocclim;
            case 86: return ref instantiateonce;
            case 87: return ref lidrup;
            case 88: return ref lrat;
            case 89: return ref lucky;
            case 90: return ref minimize;
            case 91: return ref minimizedepth;
            case 92: return ref otfs;
            case 93: return ref phase;
            case 94: return ref probe;
            case 95: return ref probehbr;
            case 96: return ref probeint;
            case 97: return ref probemaxeff;
            case 98: return ref probemineff;
            case 99: return ref probereleff;
            case 100: return ref proberounds;
            case 101: return ref profile;
            case 102: return ref quiet;
            case 103: return ref radixsortlim;
            case 104: return ref realtime;
            case 105: return ref reduce;
            case 106: return ref reduceint;
            case 107: return ref reducetarget;
            case 108: return ref reducetier1glue;
            case 109: return ref reducetier2glue;
            case 110: return ref reluctant;
            case 111: return ref reluctantmax;
            case 112: return ref rephase;
            case 113: return ref rephaseint;
            case 114: return ref report;
            case 115: return ref reportall;
            case 116: return ref reportsolve;
            case 117: return ref restart;
            case 118: return ref restartint;
            case 119: return ref restartmargin;
            case 120: return ref restartreusetrail;
            case 121: return ref restoreall;
            case 122: return ref restoreflush;
            case 123: return ref reverse;
            case 124: return ref score;
            case 125: return ref scorefactor;
            case 126: return ref seed;
            case 127: return ref shrink;
            case 128: return ref shrinkreap;
            case 129: return ref shuffle;
            case 130: return ref shufflequeue;
            case 131: return ref shufflerandom;
            case 132: return ref shufflescores;
            case 133: return ref stabilize;
            case 134: return ref stabilizefactor;
            case 135: return ref stabilizeint;
            case 136: return ref stabilizemaxint;
            case 137: return ref stabilizeonly;
            case 138: return ref stats;
            case 139: return ref subsume;
            case 140: return ref subsumebinlim;
            case 141: return ref subsumeclslim;
            case 142: return ref subsumeint;
            case 143: return ref subsumelimited;
            case 144: return ref subsumemaxeff;
            case 145: return ref subsumemineff;
            case 146: return ref subsumeocclim;
            case 147: return ref subsumereleff;
            case 148: return ref subsumestr;
            case 149: return ref target;
            case 150: return ref terminateint;
            case 151: return ref ternary;
            case 152: return ref ternarymaxadd;
            case 153: return ref ternarymaxeff;
            case 154: return ref ternarymineff;
            case 155: return ref ternaryocclim;
            case 156: return ref ternaryreleff;
            case 157: return ref ternaryrounds;
            case 158: return ref transred;
            case 159: return ref transredmaxeff;
            case 160: return ref transredmineff;
            case 161: return ref transredreleff;
            case 162: return ref verbose;
            case 163: return ref veripb;
            case 164: return ref vivify;
            case 165: return ref vivifyinst;
            case 166: return ref vivifymaxeff;
            case 167: return ref vivifymineff;
            case 168: return ref vivifyonce;
            case 169: return ref vivifyredeff;
            case 170: return ref vivifyreleff;
            case 171: return ref walk;
            case 172: return ref walkmaxeff;
            case 173: return ref walkmineff;
            case 174: return ref walknonstable;
            case 175: return ref walkredundant;
            case 176: return ref walkreleff;
            default: throw new CadicalException("invalid option index");
        }
    }
}
