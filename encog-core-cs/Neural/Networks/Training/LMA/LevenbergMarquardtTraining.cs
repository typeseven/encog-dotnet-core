//
// Encog(tm) Core v3.1 - .Net Version
// http://www.heatonresearch.com/encog/
//
// Copyright 2008-2012 Heaton Research, Inc.
//
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
//  http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.
//   
// For more information on Heaton Research copyrights, licenses 
// and trademarks visit:
// http://www.heatonresearch.com/copyright
//
using Encog.MathUtil.Error;
using Encog.MathUtil.Matrices.Decomposition;
using Encog.MathUtil.Matrices.Hessian;
using Encog.ML;
using Encog.ML.Data;
using Encog.ML.Data.Basic;
using Encog.ML.Train;
using Encog.Neural.Networks.Structure;
using Encog.Neural.Networks.Training.Propagation;
using Encog.Util.Concurrency;
using Encog.Util.Validate;

namespace Encog.Neural.Networks.Training.Lma
{
    /// <summary>
    /// Trains a neural network using a Levenberg Marquardt algorithm (LMA). This
    /// training technique is based on the mathematical technique of the same name.
    /// 
    /// The LMA interpolates between the Gauss-Newton algorithm (GNA) and the 
    /// method of gradient descent (similar to what is used by backpropagation. 
    /// The lambda parameter determines the degree to which GNA and Gradient 
    /// Descent are used.  A lower lambda results in heavier use of GNA, 
    /// whereas a higher lambda results in a heavier use of gradient descent.
    /// 
    /// Each iteration starts with a low lambda that  builds if the improvement 
    /// to the neural network is not desirable.  At some point the lambda is
    /// high enough that the training method reverts totally to gradient descent.
    /// 
    /// This allows the neural network to be trained effectively in cases where GNA
    /// provides the optimal training time, but has the ability to fall back to the
    /// more primitive gradient descent method
    ///
    /// LMA finds only a local minimum, not a global minimum.
    ///  
    /// References:
    /// 
    /// C. R. Souza. (2009). Neural Network Learning by the Levenberg-Marquardt Algorithm 
    /// with Bayesian Regularization. Website, available from: 
    /// http://crsouza.blogspot.com/2009/11/neural-network-learning-by-levenberg_18.html
    /// 
    /// http://www.heatonresearch.com/wiki/LMA
    /// http://en.wikipedia.org/wiki/Levenberg%E2%80%93Marquardt_algorithm
    /// http://en.wikipedia.org/wiki/Finite_difference_method
    /// http://mathworld.wolfram.com/FiniteDifference.html 
    /// http://www-alg.ist.hokudai.ac.jp/~jan/alpha.pdf -
    /// http://www.inference.phy.cam.ac.uk/mackay/Bayes_FAQ.html
    /// </summary>
    public class LevenbergMarquardtTraining : BasicTraining, IMultiThreadable
    {
        /// <summary>
        /// The amount to scale the lambda by.
        /// </summary>
        public const double ScaleLambda = 10.0;

        /// <summary>
        /// The max amount for the LAMBDA.
        /// </summary>
        public const double LambdaMax = 1e25;

        /// <summary>
        /// The diagonal of the hessian.
        /// </summary>
        private readonly double[] _diagonal;

        /// <summary>
        /// Utility class to compute the Hessian.
        /// </summary>
        private readonly IComputeHessian _hessian;

        /// <summary>
        /// The training set that we are using to train.
        /// </summary>
        private readonly IMLDataSet _indexableTraining;

        /// <summary>
        /// The network that is to be trained.
        /// </summary>
        private readonly BasicNetwork _network;

        /// <summary>
        /// The training set length.
        /// </summary>
        private readonly int _trainingLength;

        /// <summary>
        /// How many weights are we dealing with?
        /// </summary>
        private readonly int _weightCount;

        /// <summary>
        /// The amount to change the weights by.
        /// </summary>
        private double[] _deltas;

        /// <summary>
        /// The lambda, or damping factor. This is increased until a desirable
        /// adjustment is found.
        /// </summary>
        private double _lambda;

        /// <summary>
        /// The neural network weights and bias values.
        /// </summary>
        private double[] _weights;

        /// <summary>
        /// Construct the LMA object. Use the chain rule for Hessian calc.
        /// </summary>
        /// <param name="network">The network to train. Must have a single output neuron.</param>
        /// <param name="training">The training data to use. Must be indexable.</param>
        public LevenbergMarquardtTraining(BasicNetwork network,
                                          IMLDataSet training)
            : this(network, training, new HessianCR())
        {
        }

        /// <summary>
        /// Construct the LMA object. 
        /// </summary>
        /// <param name="network">The network to train. Must have a single output neuron.</param>
        /// <param name="training">The training data to use. Must be indexable.</param>
        /// <param name="h">The Hessian calculator to use.</param>
        public LevenbergMarquardtTraining(BasicNetwork network,
                                          IMLDataSet training, IComputeHessian h)
            : base(TrainingImplementationType.Iterative)
        {
            ValidateNetwork.ValidateMethodToData(network, training);

            Training = training;
            _indexableTraining = Training;
            this._network = network;
            _trainingLength = (int) _indexableTraining.Count;
            _weightCount = this._network.Structure.CalculateSize();
            _lambda = 0.1;
            _deltas = new double[_weightCount];
            _diagonal = new double[_weightCount];

            var input = new BasicMLData(
                _indexableTraining.InputSize);
            var ideal = new BasicMLData(
                _indexableTraining.IdealSize);

            _hessian = h;
            _hessian.Init(network, training);
        }

        /// <inheritdoc/>
        public override bool CanContinue
        {
            get { return false; }
        }

        /// <summary>
        /// The trained neural network.
        /// </summary>
        public override IMLMethod Method
        {
            get { return _network; }
        }

        /// <summary>
        /// The Hessian calculation method used.
        /// </summary>
        public IComputeHessian Hessian
        {
            get { return _hessian; }
        }

        #region IMultiThreadable Members

        /// <summary>
        /// The thread count, specify 0 for Encog to automatically select (default).  
        /// If the underlying Hessian calculator does not support multithreading, an error 
        /// will be thrown.  The default chain rule calc does support multithreading.
        /// </summary>
        public int ThreadCount
        {
            get
            {
                if (_hessian is IMultiThreadable)
                {
                    return ((IMultiThreadable) _hessian).ThreadCount;
                }
                throw new TrainingError("The Hessian object in use(" + _hessian.GetType().Name +
                                        ") does not support multi-threaded mode.");
            }
            set
            {
                if (_hessian is IMultiThreadable)
                {
                    ((IMultiThreadable) _hessian).ThreadCount = value;
                }
                else
                {
                    throw new TrainingError("The Hessian object in use(" + _hessian.GetType().Name +
                                            ") does not support multi-threaded mode.");
                }
            }
        }

        #endregion

        /// <summary>
        /// Save the diagonal of the Hessian.  Will be used to apply the lambda.
        /// </summary>
        private void SaveDiagonal()
        {
            double[][] h = _hessian.Hessian;
            for (int i = 0; i < _weightCount; i++)
            {
                _diagonal[i] = h[i][i];
            }
        }

        /// <summary>
        /// Calculate the SSE error.
        /// </summary>
        /// <returns>The SSE error with the current weights.</returns>
        private double CalculateError()
        {
            var result = new ErrorCalculation();

			IMLDataPair pair;
            for (int i = 0; i < _trainingLength; i++)
            {
                pair = _indexableTraining[i];
                IMLData actual = _network.Compute(pair.Input);
                result.UpdateError(actual, pair.Ideal, pair.Significance);
            }

            return result.CalculateSSE();
        }

        /// <summary>
        /// Apply the lambda, this will dampen the GNA.
        /// </summary>
        private void ApplyLambda()
        {
            double[][] h = _hessian.Hessian;
            for (int i = 0; i < _weightCount; i++)
            {
                h[i][i] = _diagonal[i] + _lambda;
            }
        }

        /// <summary>
        /// Perform one iteration.
        /// </summary>
        public override void Iteration()
        {
            LUDecomposition decomposition;
            PreIteration();

            _hessian.Clear();
            _weights = NetworkCODEC.NetworkToArray(_network);

            _hessian.Compute();
            double currentError = _hessian.SSE;
            SaveDiagonal();

            double startingError = currentError;
            bool done = false;

            while (!done)
            {
                ApplyLambda();
                decomposition = new LUDecomposition(_hessian.HessianMatrix);

                if (decomposition.IsNonsingular)
                {
                    _deltas = decomposition.Solve(_hessian.Gradients);

                    UpdateWeights();
                    currentError = CalculateError();

                    if (currentError < startingError)
                    {
                        _lambda /= LevenbergMarquardtTraining.ScaleLambda;
                        done = true;
                    }
                }

                if (!done)
                {
                    _lambda *= LevenbergMarquardtTraining.ScaleLambda;
                    if (_lambda > LevenbergMarquardtTraining.LambdaMax)
                    {
                        _lambda = LevenbergMarquardtTraining.LambdaMax;
                        done = true;
                    }
                }
            }

            Error = currentError;

            PostIteration();
        }

        /// <inheritdoc/>
        public override TrainingContinuation Pause()
        {
            return null;
        }

        /// <inheritdoc/>
        public override void Resume(TrainingContinuation state)
        {
        }

        /// <summary>
        /// Update the weights in the neural network.
        /// </summary>
        public void UpdateWeights()
        {
            var w = (double[]) _weights.Clone();

            for (int i = 0; i < w.Length; i++)
            {
                w[i] += _deltas[i];
            }

            NetworkCODEC.ArrayToNetwork(w, _network);
        }
    }
}
