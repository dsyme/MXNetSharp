namespace MXNetSharp
open System
open System.Runtime.InteropServices
open MXNetSharp.Interop
open System.Collections.Generic
 

[<NoComparison>]
type AuxBind = 
    {
        Name : string
        NDArray : NDArray option
        Shape : int [] option
        DataType : DataType option
        StorageType : StorageType option
    }
    static member Named(name) =     
        {
            Name = name 
            NDArray = None 
            Shape = None 
            DataType = None 
            StorageType = None
        }

[<NoComparison>]
type ArgBind = 
    {
        Name : string
        NDArray : NDArray option
        Grad : NDArray option
        OpReqType : OpReqType option
        Shape : int [] option
        DataType : DataType option
        StorageType : StorageType option
    }
    static member Named(name) =     
        {
            Name = name 
            NDArray = None 
            Grad = None 
            OpReqType = None 
            Shape = None 
            DataType = None 
            StorageType = None
        }

[<NoComparison>]
type Bind = 
    | AuxBinding of AuxBind
    | ArgBinding of ArgBind
    member x.Name = 
        match x with 
        | AuxBinding r -> r.Name
        | ArgBinding r -> r.Name
    member x.Shape = 
        match x with 
        | AuxBinding {Shape = s}
        | ArgBinding {Shape = s} -> s
    member x.DataType = 
        match x with 
        | AuxBinding {DataType = s}
        | ArgBinding {DataType = s} -> s
    member x.WithNDArray ndarray = 
        match x with 
        | AuxBinding(a) -> AuxBinding{a with NDArray = Some ndarray}
        | ArgBinding(a) -> ArgBinding{a with NDArray = Some ndarray}
    member x.NDArray = 
        match x with 
        | AuxBinding(a) -> a.NDArray
        | ArgBinding(a) -> a.NDArray
    member x.HasNDArray = x.NDArray.IsSome
    static member Arg(name, ?ndarray : NDArray, ?grad : NDArray, ?opReqType : OpReqType, ?shape : int seq, ?dataType : DataType, ?storageType : StorageType) = 
        ArgBinding 
            {ArgBind.Named name with 
                Name = name
                NDArray = ndarray
                Grad = grad 
                OpReqType = opReqType
                Shape = shape |> Option.map Seq.toArray
                DataType = dataType 
                StorageType = storageType
            }
    static member Aux(name, ?ndarray : NDArray, ?shape : int [], ?dataType : DataType, ?storageType : StorageType) = 
        AuxBinding 
            {AuxBind.Named name with 
                Name = name
                NDArray = ndarray
                Shape = shape |> Option.map Seq.toArray
                DataType = dataType 
                StorageType = storageType
            }

type IInitializer = 
    abstract member Initialize : Bind -> unit


type Parameter(?name, ?shape, ?opReqType, ?grad, ?ndarray, ?dataType, ?storageType, ?init : IInitializer) = 
    inherit Variable()
    let shape = shape |> Option.map (Seq.toArray)
    do 
        match name with 
        | Some n -> base.Name <- n
        | None -> ()
    member x.Shape = shape
    member x.Grad = grad
    member x.OpReqType = opReqType
    member x.NDArray = ndarray
    member x.DataType = dataType 
    member x.StorageType = storageType
    member x.Initializer = init
    member x.Binding = 
       {
           Name = x.Name 
           NDArray = x.NDArray 
           Grad = x.Grad
           OpReqType = x.OpReqType
           Shape = x.Shape
           DataType = x.DataType 
           StorageType = x.StorageType
       }

type Input(?name, ?shape, ?ndarray, ?dataType, ?storageType) = 
    inherit Parameter(?name = name, 
                      ?shape = shape, 
                      opReqType = OpReqType.NullOp, 
                      grad = new NDArray(), 
                      ?ndarray = ndarray, 
                      ?dataType = dataType, 
                      ?storageType = storageType)


type Bindings(bindings : IDictionary<string, Bind>) = 
    new() = Bindings(Map.empty)
    member x.TryGetValue(name : string, [<Out>] value : Bind byref) = 
        let scc,v = bindings.TryGetValue(name)
        value <- v
        scc

    member x.WithBindings(newBindings : Bind seq) = 
        let d = Dictionary(bindings)
        newBindings |> Seq.iter (fun b -> d.[b.Name] <- b)
        Bindings d
    member x.InferShapes(symbol : Symbol) =    
        let argNames = symbol.ArgumentNames
        let result = 
            argNames
            |> Array.choose 
                (fun name -> 
                    match bindings.TryGetValue(name) with 
                    | true, ArgBinding {Shape = Some s} -> Some(name, s)
                    | _ -> None)
            |> MXSymbol.keyShapeToCsrForm uint32 
            |||> MXSymbol.inferShapePartial symbol.UnsafeHandle 
        let auxBindings = 
            (symbol.AuxiliaryStateNames, result.AuxShapes)
            ||> Array.map2 
                (fun name shape -> 
                    let shape = shape |> Array.map int
                    match bindings.TryGetValue(name) with
                    | true, AuxBinding(b) -> AuxBinding { b with Shape = Some shape}
                    //| true, _ ->  TODO: Log?
                    | _ -> AuxBinding {AuxBind.Named name with Shape = Some shape}
                )
        let outBindings = 
            (symbol.OutputNames, result.OutputShapes)
            ||> Array.map2 
                (fun name shape -> 
                    let shape = shape |> Array.map int
                    match bindings.TryGetValue(name) with
                    | true, ArgBinding(a)-> ArgBinding {a with Shape = Some shape }
                    //| true, _ ->  TODO: Log?
                    | _ -> ArgBinding {ArgBind.Named name with Shape = Some shape}
                )
        let inBindings = 
            (argNames, result.InputShapes)
            ||> Array.map2 
                (fun name shape -> 
                    let shape = shape |> Array.map int
                    match bindings.TryGetValue(name) with
                    | true, ArgBinding(a)-> ArgBinding {a with Shape = Some shape }
                    //| true, _ ->  TODO: Log?
                    | _ -> ArgBinding {ArgBind.Named name with Shape = Some shape}
                )
        x.WithBindings(seq {yield! inBindings; yield! outBindings; yield! auxBindings})
    member x.InferTypes(symbol : Symbol) =    
        let argNames = symbol.ArgumentNames
        let result = 
            argNames
            |> Array.choose 
                (fun name -> 
                    match bindings.TryGetValue(name) with 
                    | true, ArgBinding {DataType = Some dt} -> Some (name, int dt.TypeFlag)
                    | _ -> None)
            |> Array.unzip
            ||> MXSymbol.inferTypePartial symbol.UnsafeHandle 
        let auxBindings = 
            (symbol.AuxiliaryStateNames, result.AuxTypes)
            ||> Array.map2 
                (fun name t -> 
                    match bindings.TryGetValue(name) with
                    | true, AuxBinding a -> AuxBinding { a with DataType = DataType.FromInt t}
                    //| true, _ ->  TODO: Log?
                    | _ ->  AuxBinding { AuxBind.Named name with DataType = DataType.FromInt t} 
                )
        let outBindings = 
            (symbol.OutputNames, result.OutputTypes)
            ||> Array.map2 
                (fun name t -> 
                    match bindings.TryGetValue(name) with
                    | true, ArgBinding a -> ArgBinding {a with DataType = DataType.FromInt t}
                    //| true, _ ->  TODO: Log?
                    | _ -> ArgBinding { ArgBind.Named name with DataType = DataType.FromInt t} 
                )
        let inBindings = 
            (argNames, result.InputTypes)
            ||> Array.map2 
                (fun name t -> 
                    match bindings.TryGetValue(name) with
                    | true, ArgBinding a -> ArgBinding {a with DataType = DataType.FromInt t}
                    //| true, _ ->  TODO: Log?
                    | _ -> ArgBinding { ArgBind.Named name with DataType = DataType.FromInt t} 
                )
        x.WithBindings(seq {yield! inBindings; yield! outBindings; yield! auxBindings})
    member x.Bindings = bindings
    interface IEnumerable<Bind> with 
        member x.GetEnumerator() = bindings.Values.GetEnumerator()
        member x.GetEnumerator() = bindings.Values.GetEnumerator() :> System.Collections.IEnumerator


module Bindings = 
    let mapAux f (bm : Bindings) = 
        bm
        |> Seq.map 
            (function 
             | AuxBinding a -> f a |> AuxBinding
             | x -> x
            )
        |> Seq.map (fun (x : Bind) -> x.Name, x)
        |> dict 
        |> Bindings
    let mapArg f (bm : Bindings) = 
        bm
        |> Seq.map 
            (function 
             | ArgBinding a -> f a |> ArgBinding
             | x -> x
            )
        |> Seq.map (fun (x : Bind) -> x.Name, x)
        |> dict 
        |> Bindings
    let map f (bm : Bindings) = 
        bm
        |> Seq.map f
        |> Seq.map (fun (x : Bind) -> x.Name, x)
        |> dict 
        |> Bindings
    let ofSeq l = Bindings().WithBindings l
    let inferShapes (s : Symbol) (bm : Bindings) = bm.InferShapes s
    let mapSymbolArgs (symbol : Symbol) f (bm : Bindings) = 
        let argNames = symbol.ArgumentNames |> Set.ofSeq
        bm
        |> mapArg
            (fun a ->
                if argNames.Contains a.Name then 
                    f a
                else
                    a
            )
    let freezeGraph (symbol : Symbol) (bm : Bindings) = 
        bm |> mapSymbolArgs symbol (fun a -> {a with OpReqType = Some NullOp} )



type SafeExecutorHandle(owner) = 
    inherit SafeHandle(0n, true)
    new() = new SafeExecutorHandle(true)
    new(ptr,owner) as this = new SafeExecutorHandle(owner) then this.SetHandle(ptr)
    override x.IsInvalid = x.handle <= 0n
    override x.ReleaseHandle() = CApi.MXExecutorFree x.handle = 0
    member internal x.UnsafeHandle = 
        if not x.IsClosed then
            x.handle
        else
            ObjectDisposedException("SafeExecutorHandle", "Executor handle has been closed") |> raise

 
type Executor(handle : SafeExecutorHandle, symbol, context, contextMap, inArgs, argGrad, gradReqType, auxStates, sharedExecutor, outputs, bindMap) =   
    let mutable disposed = false
    new(symbol : Symbol, context : Context, contextMap : IDictionary<string,Context>, inArgs, argGrad, gradReqType, auxStates, sharedExecutor : Executor option, bindMap : Bindings option) = 
        let inArgs = inArgs |> Seq.toArray
        let argGrad = argGrad |> Seq.toArray
        let gradReqType = gradReqType |> Seq.toArray
        let auxStates = auxStates |> Seq.toArray
        let inArgsHandles = inArgs |> Array.map (fun (x : NDArray) -> x.NDArrayHandle.UnsafeHandle)
        let argGradHandles = argGrad |> Array.map (fun (x : NDArray) -> x.NDArrayHandle.UnsafeHandle)
        let gradReqTypeHandles = gradReqType |> Array.map (fun (x : OpReqType) -> uint32 x.OpReqTypeInt)
        let auxStatesHandles = auxStates |> Array.map (fun (x : NDArray) -> x.NDArrayHandle.UnsafeHandle)
        let mapKeys,mapDevTypes,mapDevIds = 
            if contextMap.Count = 0 then 
                null,null,null
            else
                contextMap 
                |> Seq.map 
                    (fun kvp ->
                        kvp.Key, int kvp.Value.DeviceType, kvp.Value.DeviceId
                    )
                |> Seq.toArray
                |> Array.unzip3
        let sharedExecutorHandle = 
            match sharedExecutor with 
            | Some x ->
                x.UnsafeHandle
            | None -> 0n
        let h = MXExecutor.bindEX symbol.UnsafeHandle (int context.DeviceType) context.DeviceId mapKeys mapDevTypes mapDevIds inArgsHandles argGradHandles gradReqTypeHandles auxStatesHandles sharedExecutorHandle
        let safeHandle = new SafeExecutorHandle(h, true)
        let outputs = MXExecutor.outputs h |> Array.map (fun h -> new NDArray(new SafeNDArrayHandle(h, true)))
        // NOTE: We need to make sure all references get stored to prevent handles from being freed.
        new Executor(safeHandle,symbol,context,contextMap,inArgs,argGrad,gradReqType,auxStates,sharedExecutor,outputs, bindMap)
    new(symbol : Symbol, context : Context, contextMap : IDictionary<string,Context>, inArgs, argGrad, gradReqType, auxStates, sharedExecutor : Executor option) = 
        new Executor(symbol, context, contextMap, inArgs, argGrad, gradReqType, auxStates, sharedExecutor, None)
    new(symbol : Symbol, context, inArgs, argGrad, gradReqType, auxStates, bindMap) = 
        new Executor(symbol, context, Map.empty, inArgs,argGrad,gradReqType,auxStates,None,bindMap)
    new(symbol : Symbol, context, inArgs, argGrad, gradReqType, auxStates) = 
        new Executor(symbol, context, Map.empty, inArgs,argGrad,gradReqType,auxStates,None,None)
    new(symbol : Symbol, context, bindings : Bindings) = 
        let args = symbol.ArgumentNames
        let inArgs, argGrad, gradReqType = 
            args 
            |> Array.map 
                (fun name ->
                    match bindings.TryGetValue(name) with 
                    | true, (ArgBinding b) ->  //TODO: exception clean up
                        let a = match b.NDArray with Some a -> a | None -> failwithf "Must provide %s" name
                        let g = match b.Grad with Some a -> a | None -> failwithf "Must provide %s" name
                        let t = match b.OpReqType with Some a -> a | None -> failwithf "Must provide %s" name
                        a,g,t
                    | _ -> failwithf "No binding for %s" name
                )
            |> Array.unzip3
        let aux = 
            symbol.AuxiliaryStateNames
            |> Array.map 
                (fun name ->
                    match bindings.TryGetValue(name) with 
                    | true, (AuxBinding b) ->  //TODO: exception clean up
                        let a = match b.NDArray with Some a -> a | None -> failwithf "Must provide %s" name
                        a
                    | _ -> failwithf "No binding for %s" name
                )
        new Executor(symbol, context, inArgs, argGrad, gradReqType, aux, Some bindings)
    member x.Print() = MXExecutor.print handle.UnsafeHandle
    member x.BindMap =  
        match bindMap with 
        | Some bm -> bm
        | None ->
            let args = Array.zip3 inArgs argGrad gradReqType
            seq {
                yield!
                    (symbol.ArgumentNames, args)
                    ||> Seq.map2
                        (fun name (a,g,t) ->
                            ArgBinding 
                                { 
                                    Name = name
                                    Shape = Some a.Shape
                                    NDArray = Some a
                                    Grad = Some g
                                    OpReqType = Some t
                                    //StorageType = Some a.StorageType //TODO: ndarray storage type
                                    DataType = a.DataType 
                                    StorageType = None 
                                }
                        )
                yield!
                    (symbol.AuxiliaryStateNames, auxStates)
                    ||> Seq.map2
                        (fun name a ->
                            AuxBinding 
                                { 
                                    Name = name
                                    Shape = Some a.Shape
                                    NDArray = Some a
                                    //StorageType = Some a.StorageType //TODO: ndarray storage type
                                    DataType = a.DataType
                                    StorageType = None 
                                }
                        )
                yield!
                    (symbol.OutputNames, outputs)
                    ||> Seq.map2
                        (fun name a ->
                            ArgBinding 
                                { 
                                    Name = name
                                    Shape = Some a.Shape
                                    NDArray = Some a
                                    Grad = None
                                    OpReqType = None
                                    //StorageType = Some a.StorageType //TODO: ndarray storage type
                                    DataType = a.DataType 
                                    StorageType = None 
                                }
                        )
            }
            |> Bindings.ofSeq
    member x.Symbol = symbol
    member internal x.UnsafeHandle = handle.UnsafeHandle
    member x.Forward(isTraining : bool) = 
        let isTrain = if isTraining then 1 else 0
        MXExecutor.forward handle.UnsafeHandle isTrain
    member x.Backward() = 
        MXExecutor.backward handle.UnsafeHandle null
    member x.Backward(grads) = 
        grads
        |> Seq.map (fun (x : NDArray) -> x.NDArrayHandle.UnsafeHandle) 
        |> Seq.toArray
        |> MXExecutor.backward handle.UnsafeHandle
    member x.Outputs = outputs
    member x.Dispose(disposing) = 
        if not disposed then 
            if disposing then 
                handle.Dispose()
        disposed <- true
    member x.Dispose() = 
        x.Dispose(true)
        GC.SuppressFinalize(x)
    interface IDisposable with  
        member x.Dispose() = x.Dispose()

[<AutoOpen>]
module SymbolExtension =
    type Symbol with 
        member x.Bind(context, bindmap) = new Executor(x,context,bindmap)
        member x.Bind(context) = new Executor(x,context,Bindings())