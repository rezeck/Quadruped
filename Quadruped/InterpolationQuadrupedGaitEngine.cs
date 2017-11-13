﻿using System;
using System.Collections.Concurrent;
using System.IO;
using System.Numerics;
using System.Threading;

namespace Quadruped
{
    public class InterpolationQuadrupedGaitEngine : QuadrupedGaitEngine
    {
        private int _speed = 30;

        public int Speed
        {
            get => _speed;
            set => _speed = value;
        }

        private float NextStepLength => _speed * 0.001f * TimeSincelastTick;

        private readonly ConcurrentQueue<LegPositions> _moves = new ConcurrentQueue<LegPositions>();
        private readonly ManualResetEventSlim _moveQueueSingal = new ManualResetEventSlim();

        private LegPositions _nextMove;

        private const float LegHeight = -9f;
        //private const int LegDistanceLongitudinal = 17;
        //private const int LegDistanceLateral = 10;
        private const int LegDistanceLongitudinal = 15;
        private const int LegDistanceLateral = 15;

        public LegPositions OriginalRelaxedStance => new LegPositions
        {
            LeftFront = new Vector3(-LegDistanceLateral, LegDistanceLongitudinal, LegHeight),
            RightFront = new Vector3(LegDistanceLateral, LegDistanceLongitudinal, LegHeight),
            LeftRear = new Vector3(-LegDistanceLateral, -LegDistanceLongitudinal, LegHeight),
            RightRear = new Vector3(LegDistanceLateral, -LegDistanceLongitudinal, LegHeight)
        };

        private LegPositions _relaxedStance;

        public LegPositions RelaxedStance
        {
            get => _relaxedStance.Copy();
            set => _relaxedStance = value;
        }


        private LegPositions _lastWrittenPosition;

        public bool IsComamndQueueEmpty => _moveQueueSingal.IsSet && _moves.IsEmpty;

        public InterpolationQuadrupedGaitEngine(QuadrupedIkDriver driver) : base(driver)
        {
            _relaxedStance = OriginalRelaxedStance;
            Driver.Setup();
            EnqueueInitialStandup();
            if (_moves.TryDequeue(out var deqeueuedLegPosition))
            {
                _nextMove = deqeueuedLegPosition;
            }
            StartEngine();
        }

        protected override void EngineSpin()
        {
            if (_lastWrittenPosition.MoveFinished(_nextMove))
            {
                if (_moves.TryDequeue(out var deqeueuedLegPosition))
                {
                    _moveQueueSingal.Reset();
                    _nextMove = deqeueuedLegPosition;
                }
                else
                {
                    if (_moves.IsEmpty)
                    {
                        _moveQueueSingal.Set();
                    }
                    return;
                }
            }
            try
            {
                Driver.MoveLegsSynced(_lastWrittenPosition);
            }
            catch (IOException e)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine(e);
                Console.ResetColor();
                throw;
            }
            _lastWrittenPosition.MoveTowards(_nextMove, NextStepLength);
        }

        public void EnqueueInitialStandup()
        {
            _lastWrittenPosition = Driver.ReadCurrentLegPositions();
            _nextMove = _lastWrittenPosition;
            AddStep(_lastWrittenPosition);
            float average = (_lastWrittenPosition.LeftFront.Z +
                             _lastWrittenPosition.RightFront.Z +
                             _lastWrittenPosition.LeftRear.Z +
                             _lastWrittenPosition.RightRear.Z) / 4;
            if (average > LegHeight + 2)
            {
                AddStep(new LegPositions
                {
                    LeftFront = new Vector3(-LegDistanceLateral, LegDistanceLongitudinal, 0),
                    RightFront = new Vector3(LegDistanceLateral, LegDistanceLongitudinal, 0),
                    LeftRear = new Vector3(-LegDistanceLateral, -LegDistanceLongitudinal, 0),
                    RightRear = new Vector3(LegDistanceLateral, -LegDistanceLongitudinal, 0)
                });
            }

            _moves.Enqueue(RelaxedStance);
        }

        private void AddStep(LegPositions nextStep)
        {
            _moves.Enqueue(nextStep);
            _moveQueueSingal.Reset();
        }

        public void WaitUntilCommandQueueIsEmpty(CancellationToken cancellationToken) => _moveQueueSingal.Wait(cancellationToken);

        public void WaitUntilCommandQueueIsEmpty() => _moveQueueSingal.Wait();

        public void EnqueueOneStep(Vector2 direction, LegFlags forwardMovingLegs = LegFlags.RfLrCross, float frontLegShift = 2, float rearLegShift = 1, float liftHeight = 2, bool normalize = true)
        {
            // identity comparasion to prevent error on float.NaN
            if (direction.X == 0f && direction.Y == 0f)
            {
                return;
            }
            if (normalize)
            {
                direction = direction.Normal();
            }

            if (forwardMovingLegs != LegFlags.LfRrCross && forwardMovingLegs != LegFlags.RfLrCross)
            {
                throw new ArgumentException($"{nameof(forwardMovingLegs)} has to be {nameof(LegFlags.RfLrCross)} or {nameof(LegFlags.LfRrCross)}");
            }
            LegFlags backwardsMovingLegs = forwardMovingLegs == LegFlags.LfRrCross ? LegFlags.RfLrCross : LegFlags.LfRrCross;
            var nextStep = RelaxedStance;

            // Move LR and RF forward
            nextStep = nextStep.Copy();
            nextStep.Transform(new Vector3(frontLegShift * direction.X, frontLegShift * direction.Y, liftHeight), forwardMovingLegs);
            nextStep.Transform(new Vector3(-rearLegShift * direction.X, -rearLegShift * direction.Y, 0), backwardsMovingLegs);
            AddStep(nextStep);

            nextStep = nextStep.Copy();
            nextStep.Transform(new Vector3(frontLegShift * direction.X, frontLegShift * direction.Y, -liftHeight), forwardMovingLegs);
            nextStep.Transform(new Vector3(-rearLegShift * direction.X, -rearLegShift * direction.Y, 0), backwardsMovingLegs);
            AddStep(nextStep);

            // Move all
            nextStep = nextStep.Copy();
            nextStep.Transform(new Vector3(-frontLegShift * direction.X, -frontLegShift * direction.Y, 0));
            AddStep(nextStep);

            // Move RR and LF forward
            nextStep = nextStep.Copy();
            nextStep.Transform(new Vector3(frontLegShift * direction.X, frontLegShift * direction.Y, liftHeight), backwardsMovingLegs);
            nextStep.Transform(new Vector3(-rearLegShift * direction.X, -rearLegShift * direction.Y, 0), forwardMovingLegs);
            AddStep(nextStep);

            nextStep = nextStep.Copy();
            nextStep.Transform(new Vector3(frontLegShift * direction.X, frontLegShift * direction.Y, -liftHeight), backwardsMovingLegs);
            nextStep.Transform(new Vector3(-rearLegShift * direction.X, -rearLegShift * direction.Y, 0), forwardMovingLegs);
            AddStep(nextStep);
        }

        public void EnqueueTwoSteps(Vector2 direction, LegFlags forwardMovingLegs = LegFlags.RfLrCross, float frontLegShift = 2, float rearLegShift = 1, float liftHeight = 2, bool normalize = true)
        {
            // identity comparasion to prevent error on float.NaN
            if (direction.X == 0f && direction.Y == 0f)
            {
                return;
            }
            if (normalize)
            {
                direction = direction.Normal();
            }

            if (forwardMovingLegs != LegFlags.LfRrCross && forwardMovingLegs != LegFlags.RfLrCross)
            {
                throw new ArgumentException($"{nameof(forwardMovingLegs)} has to be {nameof(LegFlags.RfLrCross)} or {nameof(LegFlags.LfRrCross)}");
            }
            LegFlags backwardsMovingLegs = forwardMovingLegs == LegFlags.LfRrCross ? LegFlags.RfLrCross : LegFlags.LfRrCross;
            var nextStep = RelaxedStance;

            // Move LR and RF forward
            nextStep = nextStep.Copy();
            nextStep.Transform(new Vector3(frontLegShift * direction.X, frontLegShift * direction.Y, liftHeight), forwardMovingLegs);
            nextStep.Transform(new Vector3(-rearLegShift * direction.X, -rearLegShift * direction.Y, 0), backwardsMovingLegs);
            AddStep(nextStep);

            nextStep = nextStep.Copy();
            nextStep.Transform(new Vector3(frontLegShift * direction.X, frontLegShift * direction.Y, -liftHeight), forwardMovingLegs);
            nextStep.Transform(new Vector3(-rearLegShift * direction.X, -rearLegShift * direction.Y, 0), backwardsMovingLegs);
            AddStep(nextStep);

            // Move all
            nextStep = nextStep.Copy();
            nextStep.Transform(new Vector3(-frontLegShift * direction.X, -frontLegShift * direction.Y, 0));
            AddStep(nextStep);

            // Move RR and LF forward for two steps
            nextStep = nextStep.Copy();
            nextStep.Transform(new Vector3(frontLegShift * 2 * direction.X, frontLegShift * 2 * direction.Y, liftHeight), backwardsMovingLegs);
            nextStep.Transform(new Vector3(-rearLegShift * 2 * direction.X, -rearLegShift * 2 * direction.Y, 0), forwardMovingLegs);
            AddStep(nextStep);

            nextStep = nextStep.Copy();
            nextStep.Transform(new Vector3(frontLegShift * 2 * direction.X, frontLegShift * 2 * direction.Y, -liftHeight), backwardsMovingLegs);
            nextStep.Transform(new Vector3(-rearLegShift * 2 * direction.X, -rearLegShift * 2 * direction.Y, 0), forwardMovingLegs);
            AddStep(nextStep);

            // Move all
            nextStep = nextStep.Copy();
            nextStep.Transform(new Vector3(-frontLegShift * direction.X, -frontLegShift * direction.Y, 0));
            AddStep(nextStep);

            // Move LR and RF forward
            nextStep = nextStep.Copy();
            nextStep.Transform(new Vector3(frontLegShift * direction.X, frontLegShift * direction.Y, liftHeight), forwardMovingLegs);
            nextStep.Transform(new Vector3(-rearLegShift * direction.X, -rearLegShift * direction.Y, 0), backwardsMovingLegs);
            AddStep(nextStep);

            nextStep = nextStep.Copy();
            nextStep.Transform(new Vector3(frontLegShift * direction.X, frontLegShift * direction.Y, -liftHeight), forwardMovingLegs);
            nextStep.Transform(new Vector3(-rearLegShift * direction.X, -rearLegShift * direction.Y, 0), backwardsMovingLegs);
            AddStep(nextStep);
        }

        public void EnqueueOneRotation(float rotation, LegFlags firstMovingLegs = LegFlags.LfRrCross, float liftHeight = 2)
        {
            // identity comparasion to prevent error on float.NaN
            if (rotation == 0)
            {
                return;
            }
            if (Math.Abs(rotation) > 25)
            {
                throw new ArgumentOutOfRangeException($"{nameof(rotation)} has to be between -25 and 25 degrees");
            }

            if (firstMovingLegs != LegFlags.LfRrCross && firstMovingLegs != LegFlags.RfLrCross)
            {
                throw new ArgumentException($"{nameof(firstMovingLegs)} has to be {nameof(LegFlags.RfLrCross)} or {nameof(LegFlags.LfRrCross)}");
            }
            LegFlags secondMovingLegs = firstMovingLegs == LegFlags.LfRrCross ? LegFlags.RfLrCross : LegFlags.LfRrCross;

            rotation /= 2;
            var nextStep = RelaxedStance;

            nextStep = nextStep.Copy();
            nextStep.Rotate(new Angle(-rotation), firstMovingLegs);
            nextStep.Transform(new Vector3(0, 0, liftHeight), firstMovingLegs);
            nextStep.Rotate(new Angle(rotation), secondMovingLegs);
            AddStep(nextStep);

            nextStep = nextStep.Copy();
            nextStep.Rotate(new Angle(-rotation), firstMovingLegs);
            nextStep.Transform(new Vector3(0, 0, -liftHeight), firstMovingLegs);
            nextStep.Rotate(new Angle(rotation), secondMovingLegs);
            AddStep(nextStep);

            nextStep = nextStep.Copy();
            nextStep.Rotate(new Angle(-rotation), secondMovingLegs);
            nextStep.Transform(new Vector3(0, 0, liftHeight), secondMovingLegs);
            nextStep.Rotate(new Angle(rotation), firstMovingLegs);
            AddStep(nextStep);

            nextStep = nextStep.Copy();
            nextStep.Rotate(new Angle(-rotation), secondMovingLegs);
            nextStep.Transform(new Vector3(0, 0, -liftHeight), secondMovingLegs);
            nextStep.Rotate(new Angle(rotation), firstMovingLegs);
            AddStep(nextStep);
        }

        public override void Dispose()
        {
            base.Dispose();
            _moveQueueSingal.Dispose();
        }
    }
}
