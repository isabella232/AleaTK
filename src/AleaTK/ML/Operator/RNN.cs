﻿using System;
using System.Linq;
using Alea;
using Alea.cuDNN;

namespace AleaTK.ML.Operator
{
    public class RNN<T> : Differentiable
    {
        public RNN(Variable<T> x, int numLayers, int hiddenSize, bool isTraining = true, double dropout = 0.0, double bias = 0.0, ulong dropoutSeed = 1337UL)
        {
            X = x;
            IsTraining = isTraining;
            NumLayers = numLayers;
            HiddenSize = hiddenSize;
            Bias = bias;
            Dropout = dropout;
            DropoutSeed = dropoutSeed;

            // X shape (batch, seqLength, inputSize)
            Util.EnsureEqual(3, X.Shape.Rank, "Input layout: (batch, seqLength, inputSize)");
            MiniBatch = (int) X.Shape[0];
            SeqLength = (int) X.Shape[1];
            InputSize = (int) X.Shape[2];

            // Y Shape (batch, seqLength, hiddenSize)
            Y = Library.Variable<T>(PartialShape.Create(MiniBatch, SeqLength, HiddenSize));

            // W shape will be determined during initialization
            W = Library.Parameter<T>();

            // state variables
            var shape = PartialShape.Create(NumLayers, MiniBatch, HiddenSize);
            var strides = Strides.Create(shape[1]*shape[2], shape[2], 1); // inner change most
            HX = Library.Variable<T>(shape);
            CX = Library.Variable<T>(shape);
            HY = Library.Variable<T>(shape);
            CY = Library.Variable<T>(shape);
            StateDesc = new TensorDescriptor();
            StateDesc.SetND(Dnn.DataTypeOf<T>(), shape.AsInt32Array, strides.AsInt32Array);

            // xDesc is an array, for each step
            shape = PartialShape.Create(MiniBatch, InputSize, 1);
            strides = Strides.Create(shape[1]*shape[2], shape[2], 1);
            var xDesc = new TensorDescriptor();
            xDesc.SetND(Dnn.DataTypeOf<T>(), shape.AsInt32Array, strides.AsInt32Array);
            XDesc = Enumerable.Repeat(xDesc, SeqLength).ToArray();

            // yDesc is an array, for each step
            shape = PartialShape.Create(MiniBatch, HiddenSize, 1);
            strides = Strides.Create(shape[1]*shape[2], shape[2], 1);
            var yDesc = new TensorDescriptor();
            yDesc.SetND(Dnn.DataTypeOf<T>(), shape.AsInt32Array, strides.AsInt32Array);
            YDesc = Enumerable.Repeat(yDesc, SeqLength).ToArray();

            // construct the graph
            AddInput(X);
            AddInput(W);
            AddOutput(Y);
            AddAuxVar(HX);
            AddAuxVar(CX);
            AddAuxVar(HY);
            AddAuxVar(CY);
            AddAuxVar(DropoutStates);
            AddAuxVar(Workspace);
            AddAuxVar(ReserveSpace);
        }

        public bool IsTraining { get; }

        public double Dropout { get; }

        public double Bias { get; }

        public ulong DropoutSeed { get; }

        public TensorDescriptor StateDesc { get; }

        public TensorDescriptor[] XDesc { get; }

        public TensorDescriptor[] YDesc { get; }

        public int NumLayers { get; }

        public int HiddenSize { get; }

        public int SeqLength { get; }

        public int MiniBatch { get; }

        public int InputSize { get; }

        public Variable<T> X { get; }

        public Variable<T> HX { get; }

        public Variable<T> CX { get; }

        public Variable<T> Y { get; }

        public Variable<T> HY { get; }

        public Variable<T> CY { get; }

        public Variable<T> W { get; }

        public readonly Variable<byte> DropoutStates = Library.AuxVariable<byte>();

        public readonly Variable<byte> Workspace = Library.AuxVariable<byte>();

        public readonly Variable<byte> ReserveSpace = Library.AuxVariable<byte>();

        public readonly Symbol DropoutDesc = new Symbol();

        public readonly Symbol WDesc = new Symbol();

        public readonly Symbol RnnDesc = new Symbol();

        public void InitializeStates(Executor executor)
        {
            const double value = 0.0;

            executor.AssignTensor(HX, AleaTK.Library.Fill(Shape.Create(HX.Shape.AsArray), ScalarOps.Conv<T>(value)));
            executor.AssignTensor(CX, AleaTK.Library.Fill(Shape.Create(CX.Shape.AsArray), ScalarOps.Conv<T>(value)));
            executor.AssignTensor(HY, AleaTK.Library.Fill(Shape.Create(HY.Shape.AsArray), ScalarOps.Conv<T>(value)));
            executor.AssignTensor(CY, AleaTK.Library.Fill(Shape.Create(HY.Shape.AsArray), ScalarOps.Conv<T>(value)));

            // we assign them directly (no gradient counter increasing)
            if (IsTraining)
            {
                executor.AssignGradientDirectly(HY, AleaTK.Library.Fill(Shape.Create(HY.Shape.AsArray), ScalarOps.Conv<T>(value)));
                executor.AssignGradientDirectly(CY, AleaTK.Library.Fill(Shape.Create(CY.Shape.AsArray), ScalarOps.Conv<T>(value)));
            }
        }

        public override void Initialize(Executor executor)
        {
            var context = executor.Context.ToGpuContext();
            var dnn = context.Dnn;

            // dropout
            var dropoutDesc = executor.DropoutDescDict[DropoutDesc];
            IntPtr dropoutStatesSize;
            dnn.DropoutGetStatesSize(out dropoutStatesSize);
            var dropoutStates = executor.GetTensor(DropoutStates, Shape.Create(dropoutStatesSize.ToInt64()));
            dropoutDesc.Set(dnn, (float)Dropout, dropoutStates.Buffer.Ptr, dropoutStatesSize, DropoutSeed);

            // rnn descriptor
            var rnnDesc = executor.RnnDescDict[RnnDesc];
            var mode = RNNMode.LSTM;
            rnnDesc.Set(HiddenSize, NumLayers, dropoutDesc, RNNInputMode.LINEAR_INPUT, DirectionMode.UNIDIRECTIONAL,
                mode, Dnn.DataTypeOf<T>());

            // weight
            var wDesc = executor.FilterDescDict[WDesc];
            IntPtr weightsSize;
            dnn.GetRNNParamsSize(rnnDesc, XDesc[0], out weightsSize, Dnn.DataTypeOf<T>());
            Util.EnsureTrue(weightsSize.ToInt64()%Gpu.SizeOf<T>() == 0);
            var shapeW = Shape.Create(weightsSize.ToInt64()/Alea.Gpu.SizeOf<T>(), 1, 1);
            wDesc.SetND(Dnn.DataTypeOf<T>(), TensorFormat.CUDNN_TENSOR_NCHW, shapeW.AsInt32Array);
            //Console.WriteLine($"RNN.W.Shape: {shapeW}");

            // workspace and rreservespace
            IntPtr workSize;
            dnn.GetRNNWorkspaceSize(rnnDesc, SeqLength, XDesc, out workSize);
            executor.GetTensor(Workspace, Shape.Create(workSize.ToInt64()));

            if (IsTraining)
            {
                IntPtr reserveSize;
                dnn.GetRNNTrainingReserveSize(rnnDesc, SeqLength, XDesc, out reserveSize);
                executor.GetTensor(ReserveSpace, Shape.Create(reserveSize.ToInt64()));
            }

            // since we are using cuDNN, we'd better make sure these varaibles are allocated
            executor.GetTensor(W, shapeW);
            if (IsTraining) executor.GetGradient(W, shapeW);
            
            executor.GetTensor(Y, (Shape.Create(Y.Shape.AsArray)));
            executor.GetTensor(HX, (Shape.Create(HX.Shape.AsArray)));
            executor.GetTensor(CX, (Shape.Create(CX.Shape.AsArray)));
            executor.GetTensor(HY, (Shape.Create(HY.Shape.AsArray)));
            executor.GetTensor(CY, (Shape.Create(CY.Shape.AsArray)));

            if (IsTraining)
            {
                executor.GetGradient(X, (Shape.Create(X.Shape.AsArray)));
                executor.GetGradient(Y, (Shape.Create(Y.Shape.AsArray)));
                executor.GetGradient(HX, (Shape.Create(HX.Shape.AsArray)));
                executor.GetGradient(CX, (Shape.Create(CX.Shape.AsArray)));
                executor.GetGradient(HY, (Shape.Create(HY.Shape.AsArray)));
                executor.GetGradient(CY, (Shape.Create(CY.Shape.AsArray)));
            }

            // set LSTM weights
            var numLinearLayers = 8; // now we fixed it, hard code LSTM

            using (var filterDesc = new FilterDescriptor())
            {
                var w = executor.GetTensor(W);
                var filterDimA = new int[3];

                for (var layer = 0; layer < NumLayers; ++layer)
                {
                    for (var linLayerId = 0; linLayerId < numLinearLayers; ++linLayerId)
                    {
                        int nbDims;
                        DataType dataType;
                        TensorFormat format;
                        int length;

                        deviceptr<T> linLayerMat;
                        dnn.GetRNNLinLayerMatrixParams(rnnDesc, layer, XDesc[0], wDesc, w.Buffer.Ptr, linLayerId,
                            filterDesc, out linLayerMat);

                        filterDesc.GetND(out dataType, out format, out nbDims, filterDimA);
                        length = filterDimA.Aggregate(ScalarOps.Mul);
                        var value = 1.0/length;

                        var linLayerMatBuffer = new Buffer<T>(context.Device, w.Memory, new Layout(Shape.Create(length)),
                            linLayerMat);
                        var linLayerMatTensor = new Tensor<T>(linLayerMatBuffer);
                        context.Assign(linLayerMatTensor, ScalarOps.Conv<T>(value));

                        deviceptr<T> linLayerBias;
                        dnn.GetRNNLinLayerBiasParams(rnnDesc, layer, XDesc[0], wDesc, w.Buffer.Ptr, linLayerId,
                            filterDesc, out linLayerBias);

                        filterDesc.GetND(out dataType, out format, out nbDims, filterDimA);
                        length = filterDimA.Aggregate(ScalarOps.Mul);

                        var linLayerBiasBuffer = new Buffer<T>(context.Device, w.Memory, new Layout(Shape.Create(length)),
                            linLayerBias);
                        var linLayerBiasTensor = new Tensor<T>(linLayerBiasBuffer);
                        context.Assign(linLayerBiasTensor, ScalarOps.Conv<T>(Bias));
                    }
                }
            }

            base.Initialize(executor);
        }

        public override void Forward(Executor executor)
        {
            var context = executor.Context.ToGpuContext();
            var dnn = context.Dnn;
            var rnnDesc = executor.RnnDescDict[RnnDesc];
            var seqLength = SeqLength;
            var xDesc = XDesc;
            var x = executor.GetTensor(X);
            var hxDesc = StateDesc;
            var hx = executor.GetTensor(HX);
            var cxDesc = StateDesc;
            var cx = executor.GetTensor(CX);
            var wDesc = executor.FilterDescDict[WDesc];
            var w = executor.GetTensor(W);
            var yDesc = YDesc;
            var y = executor.GetTensor(Y);
            var hyDesc = StateDesc;
            var hy = executor.GetTensor(HY);
            var cyDesc = StateDesc;
            var cy = executor.GetTensor(CY);
            var workspace = executor.GetTensor(Workspace);

            if (IsTraining)
            {
                var reserveSpace = executor.GetTensor(ReserveSpace);
                dnn.RNNForwardTraining(
                    rnnDesc, seqLength, xDesc, x.Buffer.Ptr, hxDesc, hx.Buffer.Ptr,
                    cxDesc, cx.Buffer.Ptr, wDesc, w.Buffer.Ptr, yDesc, y.Buffer.Ptr,
                    hyDesc, hy.Buffer.Ptr, cyDesc, cy.Buffer.Ptr,
                    workspace.Buffer.Ptr, (IntPtr)workspace.Shape.Length,
                    reserveSpace.Buffer.Ptr, (IntPtr)reserveSpace.Shape.Length);
            }
            else
            {
                dnn.RNNForwardInference(
                    rnnDesc, seqLength, xDesc, x.Buffer.Ptr, hxDesc, hx.Buffer.Ptr,
                    cxDesc, cx.Buffer.Ptr, wDesc, w.Buffer.Ptr, yDesc, y.Buffer.Ptr,
                    hyDesc, hy.Buffer.Ptr, cyDesc, cy.Buffer.Ptr,
                    workspace.Buffer.Ptr, (IntPtr) workspace.Shape.Length);
            }
        }

        public override void Backward(Executor executor)
        {
            Util.EnsureTrue(IsTraining);

            var context = executor.Context.ToGpuContext();
            var dnn = context.Dnn;

            if (executor.GetData(X).GradientAggregationCounter != 0)
            {
                throw new InvalidOperationException();
            }

            if (executor.GetData(HX).GradientAggregationCounter != 0)
            {
                throw new InvalidOperationException();
            }

            if (executor.GetData(CX).GradientAggregationCounter != 0)
            {
                throw new InvalidOperationException();
            }

            //executor.Context.Eval(executor.GetTensor(HY).Reshape(-1)).Print();

            dnn.RNNBackwardData(
                executor.RnnDescDict[RnnDesc],
                SeqLength,
                YDesc,
                executor.GetTensor(Y).Buffer.Ptr,
                YDesc,
                executor.GetGradient(Y).Buffer.Ptr,
                StateDesc,
                executor.GetGradient(HY).Buffer.Ptr,
                StateDesc,
                executor.GetGradient(CY).Buffer.Ptr,
                executor.FilterDescDict[WDesc],
                executor.GetTensor(W).Buffer.Ptr,
                StateDesc,
                executor.GetTensor(HX).Buffer.Ptr,
                StateDesc,
                executor.GetTensor(CX).Buffer.Ptr,
                XDesc,
                executor.GetGradient(X).Buffer.Ptr,
                StateDesc,
                executor.GetGradient(HX).Buffer.Ptr,
                StateDesc,
                executor.GetGradient(CX).Buffer.Ptr,
                executor.GetTensor(Workspace).Buffer.Ptr,
                (IntPtr) executor.GetTensor(Workspace).Shape.Length,
                executor.GetTensor(ReserveSpace).Buffer.Ptr,
                (IntPtr) executor.GetTensor(ReserveSpace).Shape.Length);

            if (executor.GetData(W).GradientAggregationCounter == 0)
            {
                executor.AssignGradientDirectly(W, ScalarOps.Conv<T>(0.0).AsScalar());
            }

            dnn.RNNBackwardWeights(
                executor.RnnDescDict[RnnDesc],
                SeqLength,
                XDesc,
                executor.GetTensor(X).Buffer.Ptr,
                StateDesc,
                executor.GetTensor(HX).Buffer.Ptr,
                YDesc,
                executor.GetTensor(Y).Buffer.Ptr,
                executor.GetTensor(Workspace).Buffer.Ptr,
                (IntPtr) executor.GetTensor(Workspace).Shape.Length,
                executor.FilterDescDict[WDesc],
                executor.GetGradient(W).Buffer.Ptr,
                executor.GetTensor(ReserveSpace).Buffer.Ptr,
                (IntPtr)executor.GetTensor(ReserveSpace).Shape.Length);
        }
    }
}
