namespace MXNetSharp
open System
open System.Runtime.InteropServices
open MXNetSharp.Interop


type NDArray() = 
    member x.NDArrayHandle : CApi.NDArrayHandle = failwith "" 
