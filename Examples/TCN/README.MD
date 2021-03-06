﻿# Description 

F# implementation of [An Empirical Evaluation of Generic Convolutional and Recurrent Networks for Sequence Modeling](https://arxiv.org/abs/1803.01271) by Shaojie Bai, J. Zico Kolter and Vladlen Koltun.

## Model 

The main component of the model is the "Temporal Block" specified by

```fsharp
let temporalBlock name inCount outputCount kernelSize stride dilation padding dropout (x : Symbol) = 
    let conv1 = Convolution(x, 
                            weightNormalization (name + "_conv1_weight"),
                            Variable("_conv1_bias"),
                            [kernelSize],  
                            outputCount, 
                            stride = [stride], 
                            dilate = [dilation], 
                            pad = [padding],  
                            noBias = false,
                            Name = name + "_conv1")
    let conv1Sliced = conv1.[*,*,.. -padding] 
    let relu1 = Relu(conv1Sliced, Name = name + "_relu1")
    let dropout1 = Dropout(relu1, dropout, DropoutMode.Training)
    let conv2 = Convolution(dropout1,
                            weightNormalization (name + "_conv2_weight"),
                            Variable(name + "_conv2_bias"),
                            [kernelSize],  
                            outputCount, 
                            stride = [stride], 
                            dilate = [dilation], 
                            pad = [padding],  
                            noBias = false,
                            Name = name + "_conv2")
    let conv2Sliced = conv2.[*,*,.. -padding] 
    let relu2 = Relu(conv2Sliced, Name = name + "_relu2")
    let dropout2 = Dropout(relu2, dropout, DropoutMode.Training)
    let final = dropout2 :> Symbol
    let res = 
        if inCount <> outputCount then 
            Convolution(data = x, numFilter = outputCount, kernel = [1], Name = name + "_res_downsample") :> Symbol
        else 
            x        
    let relu = Relu(final + res, Name = name + "_relu")
    relu :> Symbol
```
Weight normalization is a simple reparameterization given by

```fsharp
let weightNormalization name =
    let g = Variable(name + "_g")
    let v = Variable(name + "_v")
    let n = Norm(v, 2, [1;2], keepdims = true)
    let f = BroadcastLike(g / n, v)
    let w = f * v |> withName (name + "_w")
    w :> Symbol    
```

When testing the F# implementation, I found matching the initializtion of the original code very important to having weight normalization work. The initilization used is as follows:

```fsharp
let bindings = 
    loss.Bindings
    |> Bindings.batchSize batchSize 
    |> Bindings.inferShapes loss
    |> Bindings.init 
        (fun a shape ->  
            let fanin = 
                if shape.Length = 3 then    
                    double a.Shape.Value.[1]*double a.Shape.Value.[2]
                elif shape.Length = 1 then 
                    double a.Shape.Value.[0]
                else
                    double a.Shape.Value.[1]
            let alpha = sqrt 5.0
            let gain = sqrt(2.0 / (1.0 + alpha*alpha))
            let stdv = sqrt(3.0) * (gain) / sqrt(fanin)
            MX.RandomUniformNDArray(context, -stdv, stdv, a.Shape.Value)
        )

```

This matches PyTorch's initilization of weights which is [Kaiming Uniform](https://arxiv.org/abs/1502.01852) initilization. Bias initilization was found not as important and does not match PyTorch for simplicity. It did seem using batch normalization in place of weight normaliztion was less sensitive to weight initilization.

Layers are stacked with dilation of `2^i - 1` for layer i in {0..layerCount-1} and finally mapped (for each time `t`) to "output count" by

```fsharp
let make numInputs outCount numChannels kernelSize dropout x = 
    (((0,numInputs),x :> Symbol),numChannels)
    ||> Seq.fold
        (fun ((i, lastN), last : Symbol) outCount ->
            let d = pown 2 i
            let padding = (kernelSize - 1) * d
            let y = temporalBlock (sprintf "L%d" i) lastN outCount kernelSize 1 d padding dropout last
            (i + 1, outCount), y
        )
    |> snd
    .>> SwapAxis(dim1 = 1, dim2 = 2)
    .>> FullyConnected(numHidden = outCount, Name = "final_fc", flatten = false)
    .>> SwapAxis(dim1 = 1, dim2 = 2)

```

## Adding Problem [(adding.fsx)](adding.fsx)
### Problem 
Let v_t be a sequence sampled uniformly from (0.0, 1.0) for each t in {0 .. seqLength - 1} and let I_t be a sequence such that all I_t are 0 except for I_i and I_k (i <> k with i and k uniform random) which are 1.
Output sum(v_t*I_t) over t. For example with sequence length 3, `v = [0.5; 0.2; 0.1]` and `I = [1; 0; 1]` the output should be `0.5 + 0.1 = 0.6`.

### Model
We want the last output of the sequence to match the true output. So using the general model above we have

```fsharp
let x = Input("xIn", shape = [0; inputChannels; seqLength])
let tcn = make inputChannels nclasses (Array.create levels nhidden) ksize 0.0 x
let label = Input("y", shape = [0; 1])
let z = 
    let tcn = SwapAxis(tcn,0,2)
    let tcn = SwapAxis(tcn,1,2)
    SequenceLast(tcn)

let loss = (label - z) .>> Square() .>> Mean() .>> MakeLoss()

```

### Output
Using the same parameters as the defaults in the PyTorch implementation the results match closely:
```
1          train:0.177815        test:0.143659        elapsed:00:00:41.6625711
2          train:0.091720        test:0.003471        elapsed:00:00:41.3090274
3          train:0.002364        test:0.000843        elapsed:00:00:41.1978766
4          train:0.001132        test:0.000582        elapsed:00:00:41.2609286
5          train:0.000779        test:0.000651        elapsed:00:00:41.3822438
6          train:0.000684        test:0.000912        elapsed:00:00:41.4504503
7          train:0.000506        test:0.000470        elapsed:00:00:41.5191844
8          train:0.000460        test:0.000206        elapsed:00:00:41.5551348
9          train:0.000392        test:0.000140        elapsed:00:00:41.5386694
10         train:0.000321        test:0.000091        elapsed:00:00:41.4927364
```


## Sequntial MNIST [(MNIST.fsx)](MNIST.fsx)
### Problem
Identify MNIST digits by treating each pixel as a single item in a sequence.

### Model

The last output of the sequence is used to classify the digit (using Argmax). Loss is negative log-liklihood and `correct` is calculated to display testset accuracy.

```fsharp
let x = Input("xIn", shape = [0; 1; seqLength])
let tcn = make 1 nclasses (Array.create levels nhidden) ksize dropout x
let label = Input("y", shape = [0; 1])

let z = 
    let tcn = SwapAxis(tcn,0,2)
    let tcn = SwapAxis(tcn,1,2)
    SequenceLast(tcn)

let correct = Argmax(z, axis = 1) .= Reshape(label, [-1]) .>> Sum()
let loss = -Mean(Pick(LogSoftmax(z, axis=1), Reshape(label, [-1]))) .>> MakeLoss()
let outp = SymbolGroup([loss :> Symbol; correct :> Symbol])

```

### Output

```
1          train:0.333846        test loss:0.113528        test acc:0.967548        elapsed:00:00:59.9895152
2          train:0.134682        test loss:0.082103        test acc:0.974359        elapsed:00:00:59.7280397
3          train:0.102840        test loss:0.070891        test acc:0.977965        elapsed:00:01:00.0386445
4          train:0.087685        test loss:0.060512        test acc:0.980669        elapsed:00:01:00.1369914
5          train:0.074136        test loss:0.061168        test acc:0.981270        elapsed:00:01:00.3091698
6          train:0.068078        test loss:0.056768        test acc:0.981771        elapsed:00:01:00.2082732
7          train:0.061308        test loss:0.052581        test acc:0.982973        elapsed:00:01:00.0804604
8          train:0.055402        test loss:0.053301        test acc:0.983373        elapsed:00:01:00.0303374
9          train:0.052518        test loss:0.048700        test acc:0.984575        elapsed:00:00:59.8584486
10         train:0.047068        test loss:0.045140        test acc:0.985577        elapsed:00:00:59.9931980
```
