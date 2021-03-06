using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using ProtoCore.BuildData;
using ProtoCore.DSASM;
using ProtoCore.Exceptions;
using ProtoCore.Lang;
using ProtoCore.Lang.Replication;
using ProtoCore.Utils;
using StackFrame = ProtoCore.DSASM.StackFrame;

namespace ProtoCore
{
    public class CallSite
    {
        private readonly int classScope;
        private readonly string methodName;
        private readonly FunctionTable globalFunctionTable;
        private readonly ExecutionMode executionMode;


        public CallSite(int classScope, string methodName, FunctionTable globalFunctionTable, ExecutionMode execMode)
        {
            Debug.Assert(methodName != null);
            Debug.Assert(globalFunctionTable != null);

            executionMode = execMode;
            this.classScope = classScope;
            this.methodName = methodName;
            this.globalFunctionTable = globalFunctionTable;

            if (execMode == ExecutionMode.Parallel)
                throw new CompilerInternalException(
                    "Parrallel Mode is not yet implemented {46F83CBB-9D37-444F-BA43-5E662784B1B3}");
        }

        /// <summary>
        /// Internal support method for reporting a method that can't be located
        /// </summary>
        /// <returns></returns>
        private StackValue ReportMethodNotFound(Core core, List<StackValue> arguments)
        {
            core.RuntimeStatus.LogMethodResolutionWarning(core, methodName, classScope, arguments);
            return StackUtils.BuildNull();
        }

        private StackValue ReportMethodNotAccessible(Core core)
        {
            core.RuntimeStatus.LogMethodNotAccessibleWarning(core, methodName);
            return StackUtils.BuildNull();
        }

        /// <summary>
        /// Get complete match attempts to locate a function endpoint where 1 FEP matches all of the requirements for dispatch
        /// </summary>
        /// <param name="context"></param>
        /// <param name="arguments"></param>
        /// <param name="funcGroup"></param>
        /// <param name="replicationControl"></param>
        /// <param name="stackFrame"></param>
        /// <param name="core"></param>
        /// <param name="log"></param>
        /// <returns></returns>
        private FunctionEndPoint Case1GetCompleteMatchFEP(ProtoCore.Runtime.Context context, List<StackValue> arguments,
                                                          FunctionGroup funcGroup,
                                                          ReplicationControl replicationControl, StackFrame stackFrame,
                                                          Core core, StringBuilder log)
        {
            log.AppendLine("Attempting Dispatch with ---- RC: " + replicationControl);

            //Exact match
            List<FunctionEndPoint> exactTypeMatchingCandindates =
                funcGroup.GetExactTypeMatches(context, arguments, replicationControl.Instructions, stackFrame, core);

            FunctionEndPoint fep = null;

            if (exactTypeMatchingCandindates.Count > 0)
            {
                if (exactTypeMatchingCandindates.Count == 1)
                {
                    //Exact match
                    fep = exactTypeMatchingCandindates[0];
                    log.AppendLine("1 exact match found - FEP selected" + fep);
                }
                else
                {
                    //Exact match with upcast
                    fep = SelectFEPFromMultiple(stackFrame,
                                                core,
                                                exactTypeMatchingCandindates, arguments);

                    log.AppendLine(exactTypeMatchingCandindates.Count + "exact matches found - FEP selected" + fep);
                }
            }

            return fep;
        }

        private void ComputeFeps(StringBuilder log, ProtoCore.Runtime.Context context, List<StackValue> arguments, FunctionGroup funcGroup, ReplicationControl replicationControl,
                                      List<List<int>> partialReplicationGuides, StackFrame stackFrame, Core core, 
            out List<FunctionEndPoint> resolvesFeps, out List<ReplicationInstruction> replicationInstructions)
        {


            //With replication guides only

            //Exact match
            //Match with single elements
            //Match with single elements with upcast

            //Try replication without type cast

            //Match with type conversion
            //Match with type conversion with upcast

            //Try replication + type casting

            //Try replication + type casting + Array promotion

            #region First Case: Replicate only according to the replication guides

            {
                log.AppendLine("Case 1: Exact Match");

                FunctionEndPoint fep = Case1GetCompleteMatchFEP(context, arguments, funcGroup, replicationControl,
                                                                stackFrame,
                                                                core, log);
                if (fep != null)
                {
                    //log.AppendLine("Resolution completed in " + sw.ElapsedMilliseconds + "ms");
                    if (core.Options.DumpFunctionResolverLogic)
                        core.DSExecutable.EventSink.PrintMessage(log.ToString());

                    resolvesFeps = new List<FunctionEndPoint>() {fep};
                    replicationInstructions = replicationControl.Instructions;

                    return;
                }
            }

            #endregion

            #region Case 2: Replication with no type cast

            {
                log.AppendLine("Case 2: Beginning Auto-replication, no casts");

                //Build the possible ways in which we might replicate
                List<List<ReplicationInstruction>> replicationTrials =
                    Replicator.BuildReplicationCombinations(replicationControl.Instructions, arguments, core);


                foreach (List<ReplicationInstruction> replicationOption in replicationTrials)
                {
                    ReplicationControl rc = new ReplicationControl() { Instructions = replicationOption };

                    log.AppendLine("Attempting replication control: " + rc);

                    List<List<StackValue>> reducedParams = Replicator.ComputeAllReducedParams(arguments,
                                                                                              rc.Instructions, core);
                    int resolutionFailures;

                    Dictionary<FunctionEndPoint, int> lookups = funcGroup.GetExactMatchStatistics(
                        context, reducedParams, stackFrame, core,
                        out resolutionFailures);


                    if (resolutionFailures > 0)
                        continue;

                    log.AppendLine("Resolution succeeded against FEP Cluster");
                    foreach (FunctionEndPoint fep in lookups.Keys)
                        log.AppendLine("\t - " + fep);

                    List<FunctionEndPoint> feps = new List<FunctionEndPoint>();
                    feps.AddRange(lookups.Keys);

                    //log.AppendLine("Resolution completed in " + sw.ElapsedMilliseconds + "ms");
                    if (core.Options.DumpFunctionResolverLogic)
                        core.DSExecutable.EventSink.PrintMessage(log.ToString());

                    //Otherwise we have a cluster of FEPs that can be used to dispatch the array
                    resolvesFeps = feps;
                    replicationInstructions = rc.Instructions;

                    return;
                }
            }

            #endregion

            #region Case 3: Match with type conversion, but no array promotion

            {
                log.AppendLine("Case 3: Type conversion");


                Dictionary<FunctionEndPoint, int> candidatesWithDistances =
                    funcGroup.GetConversionDistances(context, arguments, replicationControl.Instructions,
                                                     core.DSExecutable.classTable, core);
                Dictionary<FunctionEndPoint, int> candidatesWithCastDistances =
                    funcGroup.GetCastDistances(context, arguments, replicationControl.Instructions, core.DSExecutable.classTable,
                                               core);

                List<FunctionEndPoint> candidateFunctions = GetCandidateFunctions(stackFrame, candidatesWithDistances);
                FunctionEndPoint compliantTarget = GetCompliantTarget(context, arguments,
                                                                      replicationControl.Instructions, stackFrame, core,
                                                                      candidatesWithCastDistances, candidateFunctions,
                                                                      candidatesWithDistances);

                if (compliantTarget != null)
                {
                    log.AppendLine("Resolution Succeeded: " + compliantTarget);

                    if (core.Options.DumpFunctionResolverLogic)
                        core.DSExecutable.EventSink.PrintMessage(log.ToString());

                    resolvesFeps = new List<FunctionEndPoint>() {compliantTarget};
                    replicationInstructions = replicationControl.Instructions;
                    return;
                }
            }

            #endregion

            #region Case 4: Match with type conversion and replication

            log.AppendLine("Case 4: Replication + Type conversion");
            {
                if (arguments.Any(ArrayUtils.IsArray))
                {
                    //Build the possible ways in which we might replicate
                    List<List<ReplicationInstruction>> replicationTrials =
                        Replicator.BuildReplicationCombinations(replicationControl.Instructions, arguments, core);


                    foreach (List<ReplicationInstruction> replicationOption in replicationTrials)
                    {
                        ReplicationControl rc = new ReplicationControl() { Instructions = replicationOption };

                        log.AppendLine("Attempting replication control: " + rc);

                        //@TODO: THis should use the proper reducer?

                        Dictionary<FunctionEndPoint, int> candidatesWithDistances =
                            funcGroup.GetConversionDistances(context, arguments, rc.Instructions, core.DSExecutable.classTable, core);
                        Dictionary<FunctionEndPoint, int> candidatesWithCastDistances =
                            funcGroup.GetCastDistances(context, arguments, rc.Instructions, core.DSExecutable.classTable, core);

                        List<FunctionEndPoint> candidateFunctions = GetCandidateFunctions(stackFrame,
                                                                                          candidatesWithDistances);
                        FunctionEndPoint compliantTarget = GetCompliantTarget(context, arguments,
                                                                              rc.Instructions, stackFrame, core,
                                                                              candidatesWithCastDistances,
                                                                              candidateFunctions,
                                                                              candidatesWithDistances);

                        if (compliantTarget != null)
                        {
                            log.AppendLine("Resolution Succeeded: " + compliantTarget);

                            if (core.Options.DumpFunctionResolverLogic)
                                core.DSExecutable.EventSink.PrintMessage(log.ToString());

                            resolvesFeps = new List<FunctionEndPoint>() { compliantTarget };
                            replicationInstructions = rc.Instructions;
                            return;
                        }
                    }
                }
            }

            #endregion

            #region Case 5: Match with type conversion, replication and array promotion

            log.AppendLine("Case 5: Replication + Type conversion + Array promotion");
            {
                //Build the possible ways in which we might replicate
                List<List<ReplicationInstruction>> replicationTrials =
                    Replicator.BuildReplicationCombinations(replicationControl.Instructions, arguments, core);

                //Add as a first attempt a no-replication, but allowing up-promoting
                replicationTrials.Insert(0,
                                         new List<ReplicationInstruction>()
                    );


                foreach (List<ReplicationInstruction> replicationOption in replicationTrials)
                {
                    ReplicationControl rc = new ReplicationControl() { Instructions = replicationOption };

                    log.AppendLine("Attempting replication control: " + rc);

                    //@TODO: THis should use the proper reducer?

                    Dictionary<FunctionEndPoint, int> candidatesWithDistances =
                        funcGroup.GetConversionDistances(context, arguments, rc.Instructions, core.DSExecutable.classTable, core,
                                                         true);
                    Dictionary<FunctionEndPoint, int> candidatesWithCastDistances =
                        funcGroup.GetCastDistances(context, arguments, rc.Instructions, core.DSExecutable.classTable, core);

                    List<FunctionEndPoint> candidateFunctions = GetCandidateFunctions(stackFrame,
                                                                                      candidatesWithDistances);
                    FunctionEndPoint compliantTarget = GetCompliantTarget(context, arguments,
                                                                          rc.Instructions, stackFrame, core,
                                                                          candidatesWithCastDistances,
                                                                          candidateFunctions,
                                                                          candidatesWithDistances);

                    if (compliantTarget != null)
                    {
                        log.AppendLine("Resolution Succeeded: " + compliantTarget);

                        if (core.Options.DumpFunctionResolverLogic)
                            core.DSExecutable.EventSink.PrintMessage(log.ToString());
                        resolvesFeps = new List<FunctionEndPoint>() { compliantTarget };
                        replicationInstructions = rc.Instructions;
                        return;
                    }
                }
            }

            #endregion


            resolvesFeps = new List<FunctionEndPoint>();
            replicationInstructions = replicationControl.Instructions;
        }

        public StackValue DispatchNew(ProtoCore.Runtime.Context context, List<StackValue> arguments,
                                      List<List<int>> partialReplicationGuides, StackFrame stackFrame, Core core)
        {


            Stopwatch sw = new Stopwatch();
            sw.Start();


            StringBuilder log = new StringBuilder();

            log.AppendLine("Method name: " + methodName);

            #region Get Function Group

            //@PERF: Possible optimisation point here, to deal with static dispatches that don't need replication analysis
            //Handle resolution Pass 1: Name -> Method Group
            FunctionGroup funcGroup = GetFuncGroup(core);

            if (funcGroup == null)
            {
                log.AppendLine("Function group not located");
                log.AppendLine("Resolution failed in: " + sw.ElapsedMilliseconds);

                if (core.Options.DumpFunctionResolverLogic)
                    core.DSExecutable.EventSink.PrintMessage(log.ToString());

                return ReportMethodNotFound(core, arguments);
            }

            //check accesibility of function group
            bool methodAccessible = IsFunctionGroupAccessible(core, ref funcGroup);
            if (!methodAccessible)
                return ReportMethodNotAccessible(core);

            //If we got here then the function group got resolved
            log.AppendLine("Function group resolved: " + funcGroup);
            #endregion

            //Replication Control is an ordered list of the elements that we have to replicate over
            //Ordering implies containment, so element 0 is the outer most forloop, element 1 is nested within it etc.
            //Take the explicit replication guides and build the replication structure
            //Turn the replication guides into a guide -> List args data structure
            ReplicationControl replicationControl =
                Replicator.Old_ConvertGuidesToInstructions(partialReplicationGuides);

            log.AppendLine("Replication guides processed to: " + replicationControl);

            //Get the fep that are resolved
            List<FunctionEndPoint> resolvesFeps;
            List<ReplicationInstruction> replicationInstructions;

            ComputeFeps(log, context, arguments, funcGroup, replicationControl, partialReplicationGuides, stackFrame, core, out resolvesFeps, out replicationInstructions);


            if (resolvesFeps.Count == 0)
            {
                log.AppendLine("Resolution Failed");

                if (core.Options.DumpFunctionResolverLogic)
                    core.DSExecutable.EventSink.PrintMessage(log.ToString());

                return ReportMethodNotFound(core, arguments);
            }

            StackValue ret = Execute(resolvesFeps, context, arguments, replicationInstructions, stackFrame, core, funcGroup);

            return ret;

            
        }

        private bool IsFunctionGroupAccessible(Core core, ref FunctionGroup funcGroup)
        {
            bool methodAccessible = true;
            if (classScope != Constants.kGlobalScope)
            {
                // If last stack frame is not member function, then only public 
                // functions are acessible in this context. 
                int callerci, callerfi;
                core.CurrentExecutive.CurrentDSASMExec.GetCallerInformation(out callerci, out callerfi);
                if (callerci == Constants.kGlobalScope ||
                    (classScope != callerci && !core.DSExecutable.classTable.ClassNodes[classScope].IsMyBase(callerci)))
                {
                    bool hasFEP = funcGroup.FunctionEndPoints.Count > 0;
                    FunctionGroup visibleFuncGroup = new FunctionGroup();
                    visibleFuncGroup.CopyPublic(funcGroup.FunctionEndPoints);
                    funcGroup = visibleFuncGroup;

                    if (hasFEP && funcGroup.FunctionEndPoints.Count == 0)
                    {
                        methodAccessible = false;
                    }
                }
            }
            return methodAccessible;
        }

        /// <summary>
        /// Get the function group associated with this callsite
        /// </summary>
        /// <param name="core"></param>
        /// <returns></returns>
        private FunctionGroup GetFuncGroup(Core core)
        {
            FunctionGroup funcGroup = null;
            List<int> clist = new List<int> {classScope};
            int i = 0;

            while (i < clist.Count)
            {
                int cidx = clist[i];

                funcGroup = globalFunctionTable.GetFunctionGroup(cidx + 1, methodName);
                if (funcGroup != null)
                {
                    break;
                }
                else
                {
                    clist.AddRange(core.DSExecutable.classTable.ClassNodes[cidx].baseList);
                    ++i;
                }
            }
            return funcGroup;
        }


        /// <summary>
        /// Fast Dispatch handles the whole of a function call internally without allowing replicated debugging
        /// This should be used in Run Mode and Parallel execution mode
        /// This is the fastest way of dispatching to a callsite
        /// </summary>
        /// <param name="context"></param>
        /// <param name="arguments"></param>
        /// <param name="partialReplicationGuides"></param>
        /// <param name="stackFrame"></param>
        /// <param name="core"></param>
        /// <returns></returns>
        public StackValue FastDispatch(ProtoCore.Runtime.Context context, List<StackValue> arguments,
                                       List<List<int>> partialReplicationGuides, StackFrame stackFrame, Core core)
        {
            return DispatchNew(context, arguments, partialReplicationGuides, stackFrame, core);
        }




        /// <summary>
        /// This is the function that should be executed next, passing the same arugments as previously
        /// </summary>
        /// <returns></returns>
        public FunctionEndPoint ResolveForReplication(ProtoCore.Runtime.Context context, List<StackValue> arguments,
                                                      List<List<int>> partialReplicationGuides, StackFrame stackFrame,
                                                      Core core, ContinuationStructure continuation)
        {

             //throw new NotImplementedException();           

            //
            // Comment Jun: This simulates what the resolver is doing 
            //
            //      We just want a fep for testing
            //      Make sure you define an Increment function as such:
            //
            //      def Increment(i : int)
            //      {
            //          return = i + 1;
            //      }
            //      x = { 1, 2 };
            //      z = Increment(x);

            const string testFunction = "Increment";
            JILFunctionEndPoint testFep = new JILFunctionEndPoint();
            testFep.procedureNode = core.DSExecutable.procedureTable[0].GetFirst(testFunction);

            // Aparajit: The following hardcodes:
            // 1. A dummy "NextDispatchArg"
            // 2. The ContinuationStructure.Done flag is manually forced to TRUE (while testing) at the last iteration or if NextDispatchArgs is null
            // 3. Pushing the next argument onto the Stack
            
            // Use continuation.NextDispatchArgs to compute next FEP
            
            StackValue currentArg = continuation.NextDispatchArgs[0];

            // The second time, the array of two elements has no more next args and so this could be set to null or Done is true
            continuation.NextDispatchArgs.Clear();
            StackValue nextArg = StackUtils.BuildInt(2);    
            continuation.NextDispatchArgs.Add(nextArg);
            continuation.Done = false;  // return true the second time

            core.Rmem.Push(currentArg);

            return testFep;
            
        }

        public StackValue ExecuteContinuation(FunctionEndPoint jilFep, StackFrame stackFrame, Core core)
        {
            // Pushing a dummy stackframe onto the Stack for the current fep
            int ci = -1;
            int fi = 0;

            // Hardcoded for Increment as member function
            if (jilFep.procedureNode == null)
            {
                ci = 14;
                jilFep.procedureNode = core.DSExecutable.classTable.ClassNodes[ci].vtable.procList[fi];
            }
            Validity.Assert(jilFep.procedureNode != null);

            if (core.Options.IDEDebugMode)
            {
                DebugFrame debugFrame = core.DebugProps.DebugStackFrame.Peek();
                debugFrame.FinalFepChosen = jilFep;
            }

            ProtoCore.DSASM.StackValue svThisPtr = stackFrame.GetAt(DSASM.StackFrame.AbsoluteIndex.kThisPtr);
            ProtoCore.DSASM.StackValue svBlockDecl = stackFrame.GetAt(DSASM.StackFrame.AbsoluteIndex.kFunctionBlock);
            int blockCaller = (int)stackFrame.GetAt(DSASM.StackFrame.AbsoluteIndex.kFunctionCallerBlock).opdata;
            int depth = (int)stackFrame.GetAt(DSASM.StackFrame.AbsoluteIndex.kStackFrameDepth).opdata;
            DSASM.StackFrameType type = (DSASM.StackFrameType)stackFrame.GetAt(DSASM.StackFrame.AbsoluteIndex.kStackFrameType).opdata;

            int locals = 0; 
            int returnAddr = (int)stackFrame.GetAt(DSASM.StackFrame.AbsoluteIndex.kReturnAddress).opdata;
            int framePointer = core.Rmem.FramePointer;
            DSASM.StackFrameType callerType = (DSASM.StackFrameType)stackFrame.GetAt(DSASM.StackFrame.AbsoluteIndex.kCallerStackFrameType).opdata;

            StackValue svCallConvention = ProtoCore.DSASM.StackUtils.BuildNode(ProtoCore.DSASM.AddressType.CallingConvention, (long)ProtoCore.DSASM.CallingConvention.CallType.kExplicit);
            // Set TX register 
            stackFrame.SetAt(DSASM.StackFrame.AbsoluteIndex.kRegisterTX, svCallConvention);

            // Set SX register 
            stackFrame.SetAt(DSASM.StackFrame.AbsoluteIndex.kRegisterSX, svBlockDecl);

            List<ProtoCore.DSASM.StackValue> registers = new List<DSASM.StackValue>();
            registers.AddRange(stackFrame.GetRegisters());

            core.Rmem.PushStackFrame(svThisPtr, ci, fi, returnAddr, (int)svBlockDecl.opdata, blockCaller, callerType, type, depth, framePointer, registers, locals, 0);

            return StackUtils.BuildNode(AddressType.ExplicitCall, jilFep.procedureNode.pc);

        }

        public StackValue JILDispatchViaNewInterpreter(ProtoCore.Runtime.Context context, List<StackValue> arguments, List<List<int>> replicationGuides,
                                                       StackFrame stackFrame, Core core)
        {
#if DEBUG

            //Minimal sanity check
            foreach (StackValue sv in arguments)
            {
                Validity.Assert(sv.metaData.type != (int)PrimitiveType.kInvalidType,
                                "Invalid object passed to JILDispatch");

                Validity.Assert(sv.optype != AddressType.Invalid,
                                "Invalid object passed to JILDispatch");
            }
#endif

            // Dispatch method
            context.IsImplicitCall = true;
            return DispatchNew(context, arguments, replicationGuides, stackFrame, core);
            //StackValue ret = obj.DsasmValue;
            //if (obj.DsasmValue.optype == AddressType.Invalid)
            //    ret = DSASM.Mirror.ExecutionMirror.Repack(obj, core.heap);
            //return ret;
        }


        public StackValue JILDispatch(List<StackValue> arguments, List<List<int>> replicationGuides,
                                      StackFrame stackFrame, Core core, Runtime.Context context)
        {
#if DEBUG

            //Minimal sanity check
            foreach (StackValue sv in arguments)
            {
                Validity.Assert(sv.metaData.type != (int)PrimitiveType.kInvalidType,
                                "Invalid object passed to JILDispatch");

                Validity.Assert(sv.optype != AddressType.Invalid,
                                "Invalid object passed to JILDispatch");
            }
#endif

            // Dispatch method
            return DispatchNew(context, arguments, replicationGuides, stackFrame, core);
        }





        private FunctionEndPoint SelectFEPFromMultiple(StackFrame stackFrame, Core core,
                                                       List<FunctionEndPoint> feps, List<StackValue> argumentsList)
        {
            StackValue svThisPtr = stackFrame.GetAt(StackFrame.AbsoluteIndex.kThisPtr);
            Validity.Assert(svThisPtr.optype == AddressType.Pointer,
                            "this pointer wasn't a pointer. {89635B06-AD53-4170-ADA5-065EB2AE5858}");

            int typeID = (int) svThisPtr.metaData.type;

            //Test for exact match
            List<FunctionEndPoint> exactFeps = new List<FunctionEndPoint>();

            foreach (FunctionEndPoint fep in feps)
                if (fep.ClassOwnerIndex == typeID)
                    exactFeps.Add(fep);

            if (exactFeps.Count == 1)
            {
                return exactFeps[0];
            }


            //Walk the class tree structure to find the method

            while (core.DSExecutable.classTable.ClassNodes[typeID].baseList.Count > 0)
            {
                Validity.Assert(core.DSExecutable.classTable.ClassNodes[typeID].baseList.Count == 1,
                                "Multiple inheritence not yet supported {B93D8D7F-AB4D-4412-8483-33DE739C0ADA}");

                typeID = core.DSExecutable.classTable.ClassNodes[typeID].baseList[0];

                foreach (FunctionEndPoint fep in feps)
                    if (fep.ClassOwnerIndex == typeID)
                        return fep;
            }

            //We weren't able to distinguish based on class hiearchy, try to sepearete based on array ranking
            List<int> numberOfArbitraryRanks = new List<int>();

            foreach (FunctionEndPoint fep in feps)
            {
                int noArbitraries = 0;

                for (int i = 0; i < argumentsList.Count; i++)
                {
                    if (fep.FormalParams[i].rank == DSASM.Constants.kArbitraryRank)
                        noArbitraries++;

                    numberOfArbitraryRanks.Add(noArbitraries);
                }
            }

            int smallest = int.MaxValue;
            List<int> indeciesOfSmallest = new List<int>();

            for (int i = 0; i < feps.Count; i++)
            {
                if (numberOfArbitraryRanks[i] < smallest)
                {
                    smallest = numberOfArbitraryRanks[i];
                    indeciesOfSmallest.Clear();
                    indeciesOfSmallest.Add(i);
                }
                else if (numberOfArbitraryRanks[i] == smallest)
                    indeciesOfSmallest.Add(i);
            }

            Validity.Assert(indeciesOfSmallest.Count > 0,
                            "Couldn't find a fep when there should have been multiple: {EB589F55-F36B-404A-91DC-8D0EDC527E72}");

            if (indeciesOfSmallest.Count == 1)
                return feps[indeciesOfSmallest[0]];


            if (!CoreUtils.IsInternalMethod(feps[0].procedureNode.name) || CoreUtils.IsGetterSetter(feps[0].procedureNode.name))
            {
                //If this has failed, we have multiple feps, which can't be distiquished by class hiearchy. Emit a warning and select one
                StringBuilder possibleFuncs = new StringBuilder();
                possibleFuncs.Append(
                    "Couldn't decide which function to execute. Please provide more specific type information. Possible functions were: ");
                foreach (FunctionEndPoint fep in feps)
                    possibleFuncs.AppendLine("\t" + fep.ToString());


                possibleFuncs.AppendLine("Error code: {DCE486C0-0975-49F9-BE2C-2E7D8CCD17DD}");

                core.RuntimeStatus.LogWarning(RuntimeData.WarningID.kAmbiguousMethodDispatch, possibleFuncs.ToString());
            }

            return feps[0];

            //Validity.Assert(false, "We failed to find a single FEP when there should have been multiple. {CA6E1A93-4CF4-4030-AD94-3BF1C3CFC5AF}");

            //throw new Exceptions.CompilerInternalException("{CA6E1A93-4CF4-4030-AD94-3BF1C3CFC5AF}");
        }

        private FunctionEndPoint GetCompliantTarget(ProtoCore.Runtime.Context context, List<StackValue> formalParams,
                                                    List<ReplicationInstruction> replicationControl,
                                                    StackFrame stackFrame, Core core,
                                                    Dictionary<FunctionEndPoint, int> candidatesWithCastDistances,
                                                    List<FunctionEndPoint> candidateFunctions,
                                                    Dictionary<FunctionEndPoint, int> candidatesWithDistances)
        {
            FunctionEndPoint compliantTarget = null;
            //Produce an ordered list of the graph costs
            Dictionary<int, List<FunctionEndPoint>> conversionCostList = new Dictionary<int, List<FunctionEndPoint>>();

            foreach (FunctionEndPoint fep in candidateFunctions)
            {
                int cost = candidatesWithDistances[fep];
                if (conversionCostList.ContainsKey(cost))
                    conversionCostList[cost].Add(fep);
                else
                    conversionCostList.Add(cost, new List<FunctionEndPoint> {fep});
            }

            List<int> conversionCosts = new List<int>(conversionCostList.Keys);
            conversionCosts.Sort();


            //TestWhetherDispatchIsDeterministic(context, formalParams, replicationControl, candidatesWithDistances, candidatesWithCastDistances, candidateFunctions);

            {
                List<FunctionEndPoint> fepsToSplit = new List<FunctionEndPoint>();

                foreach (int cost in conversionCosts)
                {
                    foreach (FunctionEndPoint funcFep in conversionCostList[cost])
                    {
                        if (funcFep.DoesPredicateMatch(context, formalParams, replicationControl))
                        {
                            compliantTarget = funcFep;
                            fepsToSplit.Add(funcFep);
                        }
                    }

                    if (compliantTarget != null)
                        break;
                }

                if (fepsToSplit.Count > 1)
                {
                    int lowestCost = candidatesWithCastDistances[fepsToSplit[0]];
                    compliantTarget = fepsToSplit[0];

                    List<FunctionEndPoint> lowestCostFeps = new List<FunctionEndPoint>();

                    foreach (FunctionEndPoint fep in fepsToSplit)
                    {
                        if (candidatesWithCastDistances[fep] < lowestCost)
                        {
                            lowestCost = candidatesWithCastDistances[fep];
                            compliantTarget = fep;
                            lowestCostFeps = new List<FunctionEndPoint>() {fep};
                        }
                        else if (candidatesWithCastDistances[fep] == lowestCost)
                        {
                            lowestCostFeps.Add(fep);
                        }
                    }

                    //We have multiple feps, e.g. form overriding
                    if (lowestCostFeps.Count > 0)
                        compliantTarget = SelectFEPFromMultiple(stackFrame, core, lowestCostFeps, formalParams);
                }
            }
            return compliantTarget;
        }

        private List<FunctionEndPoint> GetCandidateFunctions(StackFrame stackFrame,
                                                             Dictionary<FunctionEndPoint, int> candidatesWithDistances)
        {
            List<FunctionEndPoint> candidateFunctions = new List<FunctionEndPoint>();

            foreach (FunctionEndPoint fep in candidatesWithDistances.Keys)
            {
                // The first line checks if the lhs of a dot operation was a class name
                //if (stackFrame.GetAt(StackFrame.AbsoluteIndex.kThisPtr).optype == AddressType.ClassIndex
                //    && !fep.procedureNode.isConstructor
                //    && !fep.procedureNode.isStatic)

                if ((stackFrame.GetAt(StackFrame.AbsoluteIndex.kThisPtr).optype == AddressType.Pointer &&
                     stackFrame.GetAt(StackFrame.AbsoluteIndex.kThisPtr).opdata == -1 && fep.procedureNode != null
                     && !fep.procedureNode.isConstructor) && !fep.procedureNode.isStatic
                    && (fep.procedureNode.classScope != -1))
                {
                    continue;
                }

                candidateFunctions.Add(fep);
            }
            return candidateFunctions;
        }

        
        /// <summary>
        /// Excecute an arbitrary depth replication using the full slow path algorithm
        /// </summary>
        /// <param name="functionEndPoint"> </param>
        /// <param name="c"></param>
        /// <param name="formalParameters"></param>
        /// <param name="replicationInstructions"></param>
        /// <param name="stackFrame"></param>
        /// <param name="core"></param>
        /// <returns></returns>
        private StackValue ExecWithRISlowPath(List<FunctionEndPoint> functionEndPoint, ProtoCore.Runtime.Context c,
                                              List<StackValue> formalParameters,
                                              List<ReplicationInstruction> replicationInstructions,
                                              StackFrame stackFrame, Core core, FunctionGroup funcGroup)
        {
            //Recursion base case
            if (replicationInstructions.Count == 0)
                return ExecWithZeroRI(functionEndPoint, c, formalParameters, stackFrame, core, funcGroup);

            //Get the replication instruction that this call will deal with
            ReplicationInstruction ri = replicationInstructions[0];

            if (ri.Zipped)
            {
                //For each item in this plane, an array of the length of the minimum will be constructed

                //The size of the array will be the minimum size of the passed arrays
                List<int> repIndecies = ri.ZipIndecies;

                //this will hold the heap elements for all the arrays that are going to be replicated over

                List<HeapElement> heapElements = new List<HeapElement>();

                int retSize = Int32.MaxValue;

                foreach (int repIndex in repIndecies)
                {
                    //if (ArrayUtils.IsArray(formalParameters[repIndex]))
                    //    throw new NotImplementedException("Replication Case not implemented - Jagged Arrays - Slow path: {8606D4AA-9225-4F34-BE53-74270B8D0A90}");


                    HeapElement he = core.Heap.Heaplist[(int) formalParameters[repIndex].opdata];
                    heapElements.Add(he);
                    retSize = Math.Min(he.VisibleSize, retSize); //We need the smallest array
                }

                StackValue[] retSVs = new StackValue[retSize];

                if (core.Options.ExecutionMode == ExecutionMode.Parallel)
                    throw new NotImplementedException("Parallel mode disabled: {BF417AD5-9EA9-4292-ABBC-3526FC5A149E}");
                else
                {
                    for (int i = 0; i < retSize; i++)
                    {
                        //Build the call
                        List<StackValue> newFormalParams = new List<StackValue>();
                        newFormalParams.AddRange(formalParameters);

                        for (int repIi = 0; repIi < repIndecies.Count; repIi++)
                        {
                            newFormalParams[repIndecies[repIi]] = heapElements[repIi].Stack[i];
                        }

                        List<ReplicationInstruction> newRIs = new List<ReplicationInstruction>();
                        newRIs.AddRange(replicationInstructions);
                        newRIs.RemoveAt(0);

                        retSVs[i] = ExecWithRISlowPath(functionEndPoint, c, newFormalParams, newRIs, stackFrame, core,
                                                       funcGroup);

                        //retSVs[i] = Execute(c, CoerceParameters(newFormalParams, core), stackFrame, core);
                    }
                }

                StackValue ret = HeapUtils.StoreArray(retSVs, core);
                GCUtils.GCRetain(ret, core);
                return ret;
            }
            else
            {
                //With a cartesian product over an array, we are going to create an array of n
                //where the n is the product of the next item

                //We will call the subsequent reductions n times

                int cartIndex = ri.CartesianIndex;

                //this will hold the heap elements for all the arrays that are going to be replicated over


                bool supressArray = false;
                int retSize;
                HeapElement he = null;

                if (formalParameters[cartIndex].optype == AddressType.ArrayPointer)
                {
                    he = core.Heap.Heaplist[(int) formalParameters[cartIndex].opdata];
                    retSize = he.VisibleSize;
                }
                else
                {
                    retSize = 1;
                    supressArray = true;
                }


                StackValue[] retSVs = new StackValue[retSize];

                if (core.Options.ExecutionMode == ExecutionMode.Parallel)
                    throw new NotImplementedException("Parallel mode disabled: {BF417AD5-9EA9-4292-ABBC-3526FC5A149E}");
                else
                {
                    if (supressArray)
                    {
                        List<ReplicationInstruction> newRIs = new List<ReplicationInstruction>();
                        newRIs.AddRange(replicationInstructions);
                        newRIs.RemoveAt(0);

                        List<StackValue> newFormalParams = new List<StackValue>();
                        newFormalParams.AddRange(formalParameters);

                        return ExecWithRISlowPath(functionEndPoint, c, newFormalParams, newRIs, stackFrame, core,
                                                  funcGroup);
                    }

                    //Now iterate over each of these options
                    for (int i = 0; i < retSize; i++)
                    {
#if __PROTOTYPE_ARRAYUPDATE_FUNCTIONCALL

                        // Comment Jun: If the array pointer passed in was of type DS Null, 
                        // then it means this is the first time the results are being computed.
                        bool executeAll = c.ArrayPointer.optype == AddressType.Null;

                        if (executeAll || ProtoCore.AssociativeEngine.ArrayUpdate.IsIndexInElementUpdateList(i, c.IndicesIntoArgMap))
                        {
                            List<List<int>> prevIndexIntoList = new List<List<int>>();

                            foreach (List<int> dimList in c.IndicesIntoArgMap)
                            {
                                prevIndexIntoList.Add(new List<int>(dimList));
                            }


                            StackValue svPrevPtr = c.ArrayPointer;
                            if (!executeAll)
                            {
                                c.IndicesIntoArgMap = ProtoCore.AssociativeEngine.ArrayUpdate.UpdateIndexIntoList(i, c.IndicesIntoArgMap);
                                c.ArrayPointer = ProtoCore.Utils.ArrayUtils.GetArrayElementAt(c.ArrayPointer, i, core);
                            }

                            //Build the call
                            List<StackValue> newFormalParams = new List<StackValue>();
                            newFormalParams.AddRange(formalParameters);

                            if (he != null)
                            {
                                //It was an array pack the arg with the current value
                                newFormalParams[cartIndex] = he.Stack[i];
                            }

                            List<ReplicationInstruction> newRIs = new List<ReplicationInstruction>();
                            newRIs.AddRange(replicationInstructions);
                            newRIs.RemoveAt(0);

                            retSVs[i] = ExecWithRISlowPath(functionEndPoint, c, newFormalParams, newRIs, stackFrame, core, funcGroup);

                            // Restore the context properties for arrays
                            c.IndicesIntoArgMap = new List<List<int>>(prevIndexIntoList);
                            c.ArrayPointer = svPrevPtr;
                        }
                        else
                        {
                            retSVs[i] = ProtoCore.Utils.ArrayUtils.GetArrayElementAt(c.ArrayPointer, i, core);
                        }
#else
                        //Build the call
                        List<StackValue> newFormalParams = new List<StackValue>();
                        newFormalParams.AddRange(formalParameters);

                        if (he != null)
                        {
                            //It was an array pack the arg with the current value
                            newFormalParams[cartIndex] = he.Stack[i];
                        }

                        List<ReplicationInstruction> newRIs = new List<ReplicationInstruction>();
                        newRIs.AddRange(replicationInstructions);
                        newRIs.RemoveAt(0);

                        retSVs[i] = ExecWithRISlowPath(functionEndPoint, c, newFormalParams, newRIs, stackFrame, core,
                                                        funcGroup);
#endif
                    }
                }

                StackValue ret = HeapUtils.StoreArray(retSVs, core);
                GCUtils.GCRetain(ret, core);
                return ret;


                throw new ReplicationCaseNotCurrentlySupported(
                    "Slowpath Cartesian product replication not yet implemented {33BCFC09-9A5C-4887-B44D-3584C34741F7}");
            }
        }


        /// <summary>
        /// Dispatch without replication
        /// </summary>
        private StackValue ExecWithZeroRI(List<FunctionEndPoint> functionEndPoint, ProtoCore.Runtime.Context c,
                                          List<StackValue> formalParameters, StackFrame stackFrame, Core core,
                                          FunctionGroup funcGroup)
        {
            //@PERF: Todo add a fast path here for the case where we have a homogenious array so we can directly dispatch

            FunctionEndPoint finalFep = SelectFinalFep(c, functionEndPoint, formalParameters, stackFrame, core);

            /*functionEndPoint = ResolveFunctionEndPointWithoutReplication(c,funcGroup, formalParameters,
                                                                         stackFrame, core);*/


            if (functionEndPoint == null)
            {
                core.RuntimeStatus.LogWarning(ProtoCore.RuntimeData.WarningID.kMethodResolutionFailure,
                                              "Function dispatch could not be completed {2EB39E1B-557C-4819-94D8-CF7C9F933E8A}");
                return StackUtils.BuildNull();
            }

            if (core.Options.IDEDebugMode && core.ExecMode != ProtoCore.DSASM.InterpreterMode.kExpressionInterpreter)
            {
                DebugFrame debugFrame = core.DebugProps.DebugStackFrame.Peek();
                debugFrame.FinalFepChosen = finalFep;
            }

            //@TODO(Luke): Should this coerce?
            List<StackValue> coercedParameters = finalFep.CoerceParameters(formalParameters, core);

            // Correct block id where the function is defined. 
            StackValue funcBlock = stackFrame.GetAt(DSASM.StackFrame.AbsoluteIndex.kFunctionBlock);
            funcBlock.opdata = finalFep.BlockScope;
            stackFrame.SetAt(DSASM.StackFrame.AbsoluteIndex.kFunctionBlock, funcBlock);

            StackValue ret = finalFep.Execute(c, coercedParameters, stackFrame, core);

            // An explicit call requires return coercion at the return instruction
            if (ret.optype != AddressType.ExplicitCall)
            {
                ret = PerformReturnTypeCoerce(finalFep, core, ret);
            }
            return ret;
        }


        private FunctionEndPoint SelectFinalFep(ProtoCore.Runtime.Context context,
                                                List<FunctionEndPoint> functionEndPoint,
                                                List<StackValue> formalParameters, StackFrame stackFrame, Core core)
        {
            List<ReplicationInstruction> replicationControl = new List<ReplicationInstruction>();
                //We're never going to replicate so create an empty structure to allow us to use
            //the existing utility methods

            //Filter for exact matches

            List<FunctionEndPoint> exactTypeMatchingCandindates = new List<FunctionEndPoint>();

            foreach (FunctionEndPoint possibleFep in functionEndPoint)
            {
                if (possibleFep.DoesTypeDeepMatch(formalParameters, core))
                {
                    exactTypeMatchingCandindates.Add(possibleFep);
                }
            }


            //There was an exact match, so dispath to it
            if (exactTypeMatchingCandindates.Count > 0)
            {
                FunctionEndPoint fep = null;

                if (exactTypeMatchingCandindates.Count == 1)
                {
                    fep = exactTypeMatchingCandindates[0];
                }
                else
                {
                    fep = SelectFEPFromMultiple(stackFrame, core,
                                                exactTypeMatchingCandindates, formalParameters);
                }

                return fep;
            }
            else
            {
                Dictionary<FunctionEndPoint, int> candidatesWithDistances = new Dictionary<FunctionEndPoint, int>();
                Dictionary<FunctionEndPoint, int> candidatesWithCastDistances = new Dictionary<FunctionEndPoint, int>();

                foreach (FunctionEndPoint fep in functionEndPoint)
                {
                    //@TODO(Luke): Is this value for allow array promotion correct?
                    int distance = fep.ComputeTypeDistance(formalParameters, core.DSExecutable.classTable, core, false);
                    if (distance !=
                        (int) ProcedureDistance.kInvalidDistance)
                        candidatesWithDistances.Add(fep, distance);
                }

                foreach (FunctionEndPoint fep in functionEndPoint)
                {
                    int dist = fep.ComputeCastDistance(formalParameters, core.DSExecutable.classTable, core);
                    candidatesWithCastDistances.Add(fep, dist);
                }


                //funcGroup.GetConversionDistances(context, formalParams, replicationControl, core.DSExecutable.classTable, core);

                //Dictionary<FunctionEndPoint, int> candidatesWithCastDistances =
                //    funcGroup.GetCastDistances(context, formalParams, replicationControl, core.DSExecutable.classTable, core);

                List<FunctionEndPoint> candidateFunctions = GetCandidateFunctions(stackFrame, candidatesWithDistances);

                if (candidateFunctions.Count == 0)
                {
                    core.RuntimeStatus.LogWarning(RuntimeData.WarningID.kAmbiguousMethodDispatch,
                                                  RuntimeData.WarningMessage.kAmbigousMethodDispatch);
                    return null;
                }


                FunctionEndPoint compliantTarget = GetCompliantTarget(context, formalParameters, replicationControl,
                                                                      stackFrame, core, candidatesWithCastDistances,
                                                                      candidateFunctions, candidatesWithDistances);

                return compliantTarget;
            }
        }


        private StackValue Execute(List<FunctionEndPoint> functionEndPoint, ProtoCore.Runtime.Context c,
                                   List<StackValue> formalParameters,
                                   List<ReplicationInstruction> replicationInstructions, DSASM.StackFrame stackFrame,
                                   Core core, FunctionGroup funcGroup)
        {
            for (int i = 0; i < formalParameters.Count; ++i)
            {
                GCUtils.GCRetain(formalParameters[i], core);
            }

            StackValue ret;

            if (replicationInstructions.Count == 0)
            {
                c.IsReplicating = false;
                ret = ExecWithZeroRI(functionEndPoint, c, formalParameters, stackFrame, core, funcGroup);
            }
            else
            {
                c.IsReplicating = true;
                ret = ExecWithRISlowPath(functionEndPoint, c, formalParameters, replicationInstructions, stackFrame,
                                         core, funcGroup);
            }

            // Explicit calls require the GC of arguments in the function return instruction
            if (ret.optype != AddressType.ExplicitCall)
            {
                for (int i = 0; i < formalParameters.Count; ++i)
                {
                    GCUtils.GCRelease(formalParameters[i], core);
                }
            }


            if (ret.optype == AddressType.Null)
                return ret; //It didn't return a value

            return ret;
        }


        public static StackValue PerformReturnTypeCoerce(ProcedureNode procNode, Core core, StackValue ret)
        {
            Validity.Assert(procNode != null,
                            "Proc Node was null.... {976C039E-6FE4-4482-80BA-31850E708E79}");


            //Now cast ret into the return type
            Type retType = procNode.returntype;

            if (retType.UID == (int) PrimitiveType.kTypeVar)
            {
                if (retType.rank < 0)
                {
                    return ret;
                }
                else
                {
                    StackValue coercedRet = TypeSystem.Coerce(ret, procNode.returntype, core);
                        //IT was a var type, so don't cast
                    GCUtils.GCRetain(coercedRet, core);
                    GCUtils.GCRelease(ret, core);
                    return coercedRet;
                }
            }

            if (ret.optype == AddressType.Null)
                return ret; //IT was a var type, so don't cast

            if (ret.metaData.type == retType.UID &&
                ret.optype != AddressType.ArrayPointer &&
                retType.IsIndexable)
            {
                StackValue coercedRet = TypeSystem.Coerce(ret, retType, core);
                GCUtils.GCRetain(coercedRet, core);
                GCUtils.GCRelease(ret, core);
                return coercedRet;
            }


            if (ret.metaData.type == retType.UID)
            {
                return ret;
            }


            if (ArrayUtils.IsArray(ret) && procNode.returntype.IsIndexable)
            {
                StackValue coercedRet = TypeSystem.Coerce(ret, retType, core);
                GCUtils.GCRetain(coercedRet, core);
                GCUtils.GCRelease(ret, core);
                return coercedRet;
            }

            if (!core.DSExecutable.classTable.ClassNodes[(int) ret.metaData.type].ConvertibleTo(retType.UID))
            {
                //@TODO(Luke): log no-type coercion possible warning

                core.RuntimeStatus.LogWarning(RuntimeData.WarningID.kConversionNotPossible,
                                              ProtoCore.RuntimeData.WarningMessage.kConvertNonConvertibleTypes);

                return StackUtils.BuildNull();
            }
            else
            {
                StackValue coercedRet = TypeSystem.Coerce(ret, retType, core);
                GCUtils.GCRetain(coercedRet, core);
                GCUtils.GCRelease(ret, core);
                return coercedRet;
            }
        }
        public static StackValue PerformReturnTypeCoerce(FunctionEndPoint functionEndPoint, Core core, StackValue ret)
        {
            return PerformReturnTypeCoerce(functionEndPoint.procedureNode, core, ret);
        }

        /// <summary>
        /// Conservative guess as to whether this call will replicate or not
        /// This may give inaccurate answers if the node cluster doesn't actually exist
        /// </summary>
        /// <param name="context"></param>
        /// <param name="arguments"></param>
        /// <param name="stackFrame"></param>
        /// <param name="core"></param>
        /// <returns></returns>
        public bool WillCallReplicate(ProtoCore.Runtime.Context context, List<StackValue> arguments,
                                      List<List<int>> partialReplicationGuides, StackFrame stackFrame, Core core,
                                      out List<List<ReplicationInstruction>> replicationTrials)
        {
            replicationTrials = new List<List<ReplicationInstruction>>();

            if (partialReplicationGuides.Count > 0)
            {
                // Jun Comment: And at least one of them contains somthing
                for (int n = 0; n < partialReplicationGuides.Count; ++n)
                {
                    if (partialReplicationGuides[n].Count > 0)
                    {
                        return true;
                    }
                }
            }

            #region Get Function Group

            //@PERF: Possible optimisation point here, to deal with static dispatches that don't need replication analysis
            //Handle resolution Pass 1: Name -> Method Group
            FunctionGroup funcGroup = globalFunctionTable.GetFunctionGroup(classScope + 1, methodName);
            if (funcGroup == null)
            {
                return false;
            }

            #endregion

            //Replication Control is an ordered list of the elements that we have to replicate over
            //Ordering implies containment, so element 0 is the outer most forloop, element 1 is nested within it etc.
            //Take the explicit replication guides and build the replication structure
            //Turn the replication guides into a guide -> List args data structure
            ReplicationControl replicationControl =
                Replicator.Old_ConvertGuidesToInstructions(partialReplicationGuides);

            #region First Case: Replicate only according to the replication guides

            {
                FunctionEndPoint fep = Case1GetCompleteMatchFEP(context, arguments, funcGroup, replicationControl,
                                                                stackFrame,
                                                                core, new StringBuilder());
                if (fep != null)
                {
                    //found an exact match
                    return false;
                }
            }

            #endregion

            #region Case 2: Replication with no type cast

            {
                //Build the possible ways in which we might replicate
                replicationTrials =
                    Replicator.BuildReplicationCombinations(replicationControl.Instructions, arguments, core);


                foreach (List<ReplicationInstruction> replicationOption in replicationTrials)
                {
                    ReplicationControl rc = new ReplicationControl() {Instructions = replicationOption};


                    List<List<StackValue>> reducedParams = Replicator.ComputeAllReducedParams(arguments,
                                                                                              rc.
                                                                                                  Instructions, core);
                    int resolutionFailures;

                    Dictionary<FunctionEndPoint, int> lookups = funcGroup.GetExactMatchStatistics(
                        context, reducedParams, stackFrame, core,
                        out resolutionFailures);


                    if (resolutionFailures > 0)
                        continue;

                    return true; //Replicates against cluster
                }
            }

            #endregion

            #region Case 3: Match with type conversion, but no array promotion

            {
                Dictionary<FunctionEndPoint, int> candidatesWithDistances =
                    funcGroup.GetConversionDistances(context, arguments, replicationControl.Instructions,
                                                     core.DSExecutable.classTable, core);
                Dictionary<FunctionEndPoint, int> candidatesWithCastDistances =
                    funcGroup.GetCastDistances(context, arguments, replicationControl.Instructions, core.DSExecutable.classTable,
                                               core);

                List<FunctionEndPoint> candidateFunctions = GetCandidateFunctions(stackFrame, candidatesWithDistances);
                FunctionEndPoint compliantTarget = GetCompliantTarget(context, arguments,
                                                                      replicationControl.Instructions, stackFrame, core,
                                                                      candidatesWithCastDistances, candidateFunctions,
                                                                      candidatesWithDistances);

                if (compliantTarget != null)
                {
                    return false; //Type conversion but no replication
                }
            }

            #endregion

            #region Case 5: Match with type conversion, replication and array promotion

            {
                //Build the possible ways in which we might replicate
                replicationTrials =
                    Replicator.BuildReplicationCombinations(replicationControl.Instructions, arguments, core);

                //Add as a first attempt a no-replication, but allowing up-promoting
                replicationTrials.Insert(0,
                                         new List<ReplicationInstruction>()
                    );
            }

            #endregion

            return true; //It'll replicate if it suceeds
        }

        /*
            public FunctionEndPoint GetFep(ProtoCore.Runtime.Context context, List<StackValue> arguments, StackFrame stackFrame, List<List<int>> partialReplicationGuides, Core core)
            {
                StringBuilder log = new StringBuilder();

                log.AppendLine("Method name: " + methodName);

                #region Get Function Group
                //@PERF: Possible optimisation point here, to deal with static dispatches that don't need replication analysis
                //Handle resolution Pass 1: Name -> Method Group
                FunctionGroup funcGroup = null;
                List<int> clist = new List<int> { classScope };
                int i = 0;

                while (i < clist.Count)
                {
                    int cidx = clist[i];
                    if (globalFunctionTable.GlobalFuncTable[cidx + 1].ContainsKey(methodName))
                    {
                        funcGroup = globalFunctionTable.GlobalFuncTable[cidx + 1][methodName];
                        break;
                    }
                    else
                    {
                        clist.AddRange(core.DSExecutable.classTable.ClassNodes[cidx].baseList);
                        ++i;
                    }
                }

                if (funcGroup == null)
                {
                    if (core.Options.DumpFunctionResolverLogic)
                        core.DSExecutable.EventSink.PrintMessage(log.ToString());

                    return null;
                }

                if (classScope != Constants.kGlobalScope)
                {
                    int callerci, callerfi;
                    core.CurrentExecutive.CurrentDSASMExec.GetCallerInformation(out callerci, out callerfi);
                    if (callerci == Constants.kGlobalScope || (classScope != callerci && !core.DSExecutable.classTable.ClassNodes[classScope].IsMyBase(callerci)))
                    {
                        bool hasFEP = funcGroup.FunctionEndPoints.Count > 0;
                        FunctionGroup visibleFuncGroup = new FunctionGroup();
                        visibleFuncGroup.CopyPublic(funcGroup.FunctionEndPoints);
                        funcGroup = visibleFuncGroup;

                        if (hasFEP && funcGroup.FunctionEndPoints.Count == 0)
                        {
                            return null;
                        }
                    }
                }

                if (core.Options.DotOpToMethodOn)
                    if (null == funcGroup)
                    {
                        return null;
                    }
                log.AppendLine("Function group resolved: " + funcGroup);

                #endregion

                //Replication Control is an ordered list of the elements that we have to replicate over
                //Ordering implies containment, so element 0 is the outer most forloop, element 1 is nested within it etc.
                //Take the explicit replication guides and build the replication structure
                //Turn the replication guides into a guide -> List args data structure
                ReplicationControl replicationControl =
                    Replicator.Old_ConvertGuidesToInstructions(partialReplicationGuides);

                log.AppendLine("Replication guides processed to: " + replicationControl);


                #region First Case: Replicate only according to the replication guides
                {
                    log.AppendLine("Case 1: Exact Match");

                    FunctionEndPoint fep = Case1GetCompleteMatchFEP(context, arguments, funcGroup, replicationControl, stackFrame,
                                                               core, log);
                    if (fep != null)
                    {
                        return fep;
                    }

                }
                #endregion

                #region Case 2: Replication with no type cast
                {

                    log.AppendLine("Case 2: Beginning Auto-replication, no casts");

                    //Build the possible ways in which we might replicate
                    List<List<ReplicationInstruction>> replicationTrials =
                        Replicator.BuildReplicationCombinations(replicationControl.Instructions, arguments, core);

                    foreach (List<ReplicationInstruction> replicationOption in replicationTrials)
                    {
                        ReplicationControl rc = new ReplicationControl() { Instructions = replicationOption };

                        log.AppendLine("Attempting replication control: " + rc);

                        List<List<StackValue>> reducedParams = Replicator.ComputeAllReducedParams(arguments,
                                                                                                  rc.
                                                                                                      Instructions, core);
                        int resolutionFailures;

                        Dictionary<FunctionEndPoint, int> lookups = funcGroup.GetExactMatchStatistics(
                            context, reducedParams, stackFrame, core,
                            out resolutionFailures);


                        if (resolutionFailures > 0)
                            continue;

                        log.AppendLine("Resolution succeeded against FEP Cluster");
                        foreach (FunctionEndPoint fep in lookups.Keys)
                            log.AppendLine("\t - " + fep);

                        List<FunctionEndPoint> feps = new List<FunctionEndPoint>();
                        feps.AddRange(lookups.Keys);

                        if (core.Options.DumpFunctionResolverLogic)
                            core.DSExecutable.EventSink.PrintMessage(log.ToString());


                        return feps[0];
                    }
                }
                #endregion

                #region Case 3: Match with type conversion, but no array promotion
                {
                    Dictionary<FunctionEndPoint, int> candidatesWithDistances =
                    funcGroup.GetConversionDistances(context, arguments, replicationControl.Instructions, core.DSExecutable.classTable, core);
                    Dictionary<FunctionEndPoint, int> candidatesWithCastDistances =
                        funcGroup.GetCastDistances(context, arguments, replicationControl.Instructions, core.DSExecutable.classTable, core);

                    List<FunctionEndPoint> candidateFunctions = GetCandidateFunctions(stackFrame, candidatesWithDistances);
                    FunctionEndPoint compliantTarget = GetCompliantTarget(context, arguments, replicationControl.Instructions, stackFrame, core, candidatesWithCastDistances, candidateFunctions, candidatesWithDistances);

                    if (compliantTarget != null)
                    {
                        return compliantTarget;
                    }

                }
                #endregion

                #region Case 4: Match with type conversion and replication
                {
                    if (arguments.Any(ArrayUtils.IsArray))
                    {

                        //Build the possible ways in which we might replicate
                        List<List<ReplicationInstruction>> replicationTrials =
                            Replicator.BuildReplicationCombinations(replicationControl.Instructions, arguments, core);


                        foreach (List<ReplicationInstruction> replicationOption in replicationTrials)
                        {
                            ReplicationControl rc = new ReplicationControl() { Instructions = replicationOption };

                            log.AppendLine("Attempting replication control: " + rc);

                            //@TODO: THis should use the proper reducer?

                            Dictionary<FunctionEndPoint, int> candidatesWithDistances =
                                funcGroup.GetConversionDistances(context, arguments, rc.Instructions, core.DSExecutable.classTable, core);
                            Dictionary<FunctionEndPoint, int> candidatesWithCastDistances =
                                funcGroup.GetCastDistances(context, arguments, rc.Instructions, core.DSExecutable.classTable, core);

                            List<FunctionEndPoint> candidateFunctions = GetCandidateFunctions(stackFrame,
                                                                                              candidatesWithDistances);
                            FunctionEndPoint compliantTarget = GetCompliantTarget(context, arguments,
                                                                                  rc.Instructions, stackFrame, core,
                                                                                  candidatesWithCastDistances,
                                                                                  candidateFunctions,
                                                                                  candidatesWithDistances);

                            if (compliantTarget != null)
                            {
                                return compliantTarget;
                            }
                        }
                    }
                }
                #endregion

                #region Case 5: Match with type conversion, replication and array promotion
                {

                    //Build the possible ways in which we might replicate
                    List<List<ReplicationInstruction>> replicationTrials =
                        Replicator.BuildReplicationCombinations(replicationControl.Instructions, arguments, core);

                    //Add as a first attempt a no-replication, but allowing up-promoting
                    replicationTrials.Insert(0,
                        new List<ReplicationInstruction>()
                        );


                    foreach (List<ReplicationInstruction> replicationOption in replicationTrials)
                    {
                        ReplicationControl rc = new ReplicationControl() { Instructions = replicationOption };

                        log.AppendLine("Attempting replication control: " + rc);

                        //@TODO: THis should use the proper reducer?

                        Dictionary<FunctionEndPoint, int> candidatesWithDistances =
                            funcGroup.GetConversionDistances(context, arguments, rc.Instructions, core.DSExecutable.classTable, core, true);
                        Dictionary<FunctionEndPoint, int> candidatesWithCastDistances =
                            funcGroup.GetCastDistances(context, arguments, rc.Instructions, core.DSExecutable.classTable, core);

                        List<FunctionEndPoint> candidateFunctions = GetCandidateFunctions(stackFrame,
                                                                                            candidatesWithDistances);
                        FunctionEndPoint compliantTarget = GetCompliantTarget(context, arguments,
                                                                                rc.Instructions, stackFrame, core,
                                                                                candidatesWithCastDistances,
                                                                                candidateFunctions,
                                                                                candidatesWithDistances);

                        if (compliantTarget != null)
                        {
                            return compliantTarget;
                        }
                    }
                }
                #endregion

                log.AppendLine("Resolution Failed");

                if (core.Options.DumpFunctionResolverLogic)
                    core.DSExecutable.EventSink.PrintMessage(log.ToString());

                return null;
            }

            */


    
    }


    public class MethodResolutionException : Exception
    {
        public string MethodNotFound { get; private set; }

        public MethodResolutionException(string methodNotFound)
        {
            MethodNotFound = methodNotFound;
        }
    }
}