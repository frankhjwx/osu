﻿// Copyright (c) 2007-2018 ppy Pty Ltd <contact@ppy.sh>.
// Licensed under the MIT Licence - https://raw.githubusercontent.com/ppy/osu/master/LICENCE

using System;
using System.Linq;
using osu.Framework.Allocation;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Graphics.Textures;
using osu.Framework.Input.Bindings;
using osu.Framework.MathUtils;
using osu.Game.Beatmaps;
using osu.Game.Rulesets.Catch.Objects;
using osu.Game.Rulesets.Catch.Objects.Drawable;
using osu.Game.Rulesets.Catch.Replays;
using osu.Game.Rulesets.Judgements;
using osu.Game.Rulesets.Objects.Drawables;
using OpenTK;
using OpenTK.Graphics;

namespace osu.Game.Rulesets.Catch.UI
{
    public class CatcherArea : Container
    {
        public const float CATCHER_SIZE = 172;

        protected readonly Catcher MovableCatcher;

        public Func<CatchHitObject, DrawableHitObject<CatchHitObject>> GetVisualRepresentation;

        public Container ExplodingFruitTarget
        {
            set { MovableCatcher.ExplodingFruitTarget = value; }
        }

        public CatcherArea(BeatmapDifficulty difficulty = null)
        {
            RelativeSizeAxes = Axes.X;
            Height = CATCHER_SIZE;
            Child = MovableCatcher = new Catcher(difficulty)
            {
                AdditiveTarget = this,
            };
        }

        private DrawableCatchHitObject lastPlateableFruit;

        public void OnJudgement(DrawableCatchHitObject fruit, Judgement judgement)
        {
            if (judgement.IsHit && fruit.CanBePlated)
            {
                var caughtFruit = (DrawableCatchHitObject)GetVisualRepresentation?.Invoke(fruit.HitObject);

                if (caughtFruit == null) return;

                caughtFruit.RelativePositionAxes = Axes.None;
                caughtFruit.Position = new Vector2(MovableCatcher.ToLocalSpace(fruit.ScreenSpaceDrawQuad.Centre).X - MovableCatcher.DrawSize.X / 2, 0);

                caughtFruit.Anchor = Anchor.TopCentre;
                caughtFruit.Origin = Anchor.Centre;
                caughtFruit.Scale *= 0.7f;
                caughtFruit.LifetimeEnd = double.MaxValue;

                MovableCatcher.Add(caughtFruit);

                lastPlateableFruit = caughtFruit;
            }

            if (fruit.HitObject.LastInCombo)
            {
                if (judgement.IsHit)
                {
                    // this is required to make this run after the last caught fruit runs UpdateState at least once.
                    // TODO: find a better alternative
                    if (lastPlateableFruit.IsLoaded)
                        MovableCatcher.Explode();
                    else
                        lastPlateableFruit.OnLoadComplete = _ => { MovableCatcher.Explode(); };
                }
                else
                    MovableCatcher.Drop();
            }
        }

        protected override void UpdateAfterChildren()
        {
            base.UpdateAfterChildren();

            var state = GetContainingInputManager().CurrentState as CatchFramedReplayInputHandler.CatchReplayState;

            if (state?.CatcherX != null)
                MovableCatcher.X = state.CatcherX.Value;
        }

        public bool OnReleased(CatchAction action) => false;

        public bool AttemptCatch(CatchHitObject obj) => MovableCatcher.AttemptCatch(obj);

        public class Catcher : Container, IKeyBindingHandler<CatchAction>
        {
            private Texture texture;

            private Container<DrawableHitObject> caughtFruit;

            public Container ExplodingFruitTarget;

            public Container AdditiveTarget;

            public Catcher(BeatmapDifficulty difficulty = null)
            {
                RelativePositionAxes = Axes.X;
                X = 0.5f;

                Origin = Anchor.TopCentre;
                Anchor = Anchor.TopLeft;

                Size = new Vector2(CATCHER_SIZE);
                if (difficulty != null)
                    Scale = new Vector2((1.0f - 0.7f * (difficulty.CircleSize - 5) / 5) * 0.62064f);
            }

            [BackgroundDependencyLoader]
            private void load(TextureStore textures)
            {
                texture = textures.Get(@"Play/Catch/fruit-catcher-idle");

                Children = new Drawable[]
                {
                    caughtFruit = new Container<DrawableHitObject>
                    {
                        Anchor = Anchor.TopCentre,
                        Origin = Anchor.BottomCentre,
                    },
                    createCatcherSprite(),
                };
            }

            private int currentDirection;

            private bool dashing;

            protected bool Dashing
            {
                get { return dashing; }
                set
                {
                    if (value == dashing) return;

                    dashing = value;

                    Trail |= dashing;
                }
            }

            private bool trail;

            /// <summary>
            /// Activate or deactive the trail. Will be automatically deactivated when conditions to keep the trail displayed are no longer met.
            /// </summary>
            protected bool Trail
            {
                get { return trail; }
                set
                {
                    if (value == trail) return;

                    trail = value;

                    if (Trail)
                        beginTrail();
                }
            }

            private void beginTrail()
            {
                Trail &= dashing || HyperDashing;
                Trail &= AdditiveTarget != null;

                if (!Trail) return;

                var additive = createCatcherSprite();

                additive.Anchor = Anchor;
                additive.OriginPosition = additive.OriginPosition + new Vector2(DrawWidth / 2, 0); // also temporary to align sprite correctly.
                additive.Position = Position;
                additive.Scale = Scale;
                additive.Colour = HyperDashing ? Color4.Red : Color4.White;
                additive.RelativePositionAxes = RelativePositionAxes;
                additive.Blending = BlendingMode.Additive;

                AdditiveTarget.Add(additive);

                additive.FadeTo(0.4f).FadeOut(800, Easing.OutQuint).Expire();

                Scheduler.AddDelayed(beginTrail, HyperDashing ? 25 : 50);
            }

            private Sprite createCatcherSprite() => new Sprite
            {
                Size = new Vector2(CATCHER_SIZE),
                FillMode = FillMode.Fill,
                Texture = texture,
                OriginPosition = new Vector2(-3, 10) // temporary until the sprite is aligned correctly.
            };

            /// <summary>
            /// Add a caught fruit to the catcher's stack.
            /// </summary>
            /// <param name="fruit">The fruit that was caught.</param>
            public void Add(DrawableHitObject fruit)
            {
                float ourRadius = fruit.DrawSize.X / 2 * fruit.Scale.X;
                float theirRadius = 0;

                const float allowance = 6;

                while (caughtFruit.Any(f =>
                    f.LifetimeEnd == double.MaxValue &&
                    Vector2Extensions.Distance(f.Position, fruit.Position) < (ourRadius + (theirRadius = f.DrawSize.X / 2 * f.Scale.X)) / (allowance / 2)))
                {
                    float diff = (ourRadius + theirRadius) / allowance;
                    fruit.X += (RNG.NextSingle() - 0.5f) * 2 * diff;
                    fruit.Y -= RNG.NextSingle() * diff;
                }

                fruit.X = MathHelper.Clamp(fruit.X, -CATCHER_SIZE / 2, CATCHER_SIZE / 2);

                caughtFruit.Add(fruit);
            }

            /// <summary>
            /// Let the catcher attempt to catch a fruit.
            /// </summary>
            /// <param name="fruit">The fruit to catch.</param>
            /// <returns>Whether the catch is possible.</returns>
            public bool AttemptCatch(CatchHitObject fruit)
            {
                double halfCatcherWidth = CATCHER_SIZE * Math.Abs(Scale.X) * 0.5f;

                // this stuff wil disappear once we move fruit to non-relative coordinate space in the future.
                var catchObjectPosition = fruit.X * CatchPlayfield.BASE_WIDTH;
                var catcherPosition = Position.X * CatchPlayfield.BASE_WIDTH;

                var validCatch =
                    catchObjectPosition >= catcherPosition - halfCatcherWidth &&
                    catchObjectPosition <= catcherPosition + halfCatcherWidth;

                if (validCatch && fruit.HyperDash)
                {
                    HyperDashModifier = Math.Abs(fruit.HyperDashTarget.X - fruit.X) / Math.Abs(fruit.HyperDashTarget.StartTime - fruit.StartTime) / BASE_SPEED;
                    HyperDashDirection = fruit.HyperDashTarget.X - fruit.X;
                }
                else
                    HyperDashModifier = 1;

                return validCatch;
            }

            /// <summary>
            /// Whether we are hypderdashing or not.
            /// </summary>
            public bool HyperDashing => hyperDashModifier != 1;

            private double hyperDashModifier = 1;

            /// <summary>
            /// The direction in which hyperdash is allowed. 0 allows both directions.
            /// </summary>
            public double HyperDashDirection;

            /// <summary>
            /// The speed modifier resultant from hyperdash. Will trigger hyperdash when not equal to 1.
            /// </summary>
            public double HyperDashModifier
            {
                get { return hyperDashModifier; }
                set
                {
                    if (value == hyperDashModifier) return;
                    hyperDashModifier = value;

                    const float transition_length = 180;

                    if (HyperDashing)
                    {
                        this.FadeColour(Color4.OrangeRed, transition_length, Easing.OutQuint);
                        this.FadeTo(0.2f, transition_length, Easing.OutQuint);
                        Trail = true;
                    }
                    else
                    {
                        HyperDashDirection = 0;
                        this.FadeColour(Color4.White, transition_length, Easing.OutQuint);
                        this.FadeTo(1, transition_length, Easing.OutQuint);
                    }
                }
            }

            public bool OnPressed(CatchAction action)
            {
                switch (action)
                {
                    case CatchAction.MoveLeft:
                        currentDirection--;
                        return true;
                    case CatchAction.MoveRight:
                        currentDirection++;
                        return true;
                    case CatchAction.Dash:
                        Dashing = true;
                        return true;
                }

                return false;
            }

            public bool OnReleased(CatchAction action)
            {
                switch (action)
                {
                    case CatchAction.MoveLeft:
                        currentDirection++;
                        return true;
                    case CatchAction.MoveRight:
                        currentDirection--;
                        return true;
                    case CatchAction.Dash:
                        Dashing = false;
                        return true;
                }

                return false;
            }

            /// <summary>
            /// The relative space to cover in 1 millisecond. based on 1 game pixel per millisecond as in osu-stable.
            /// </summary>
            public const double BASE_SPEED = 1.0 / 512;

            protected override void Update()
            {
                base.Update();

                if (currentDirection == 0) return;

                var direction = Math.Sign(currentDirection);

                double dashModifier = Dashing ? 1 : 0.5;

                if (hyperDashModifier != 1 && (HyperDashDirection == 0 || direction == Math.Sign(HyperDashDirection)))
                    dashModifier = hyperDashModifier;

                Scale = new Vector2(Math.Abs(Scale.X) * direction, Scale.Y);
                X = (float)MathHelper.Clamp(X + direction * Clock.ElapsedFrameTime * BASE_SPEED * dashModifier, 0, 1);
            }

            /// <summary>
            /// Drop any fruit off the plate.
            /// </summary>
            public void Drop()
            {
                var fruit = caughtFruit.ToArray();

                foreach (var f in fruit)
                {
                    if (ExplodingFruitTarget != null)
                    {
                        f.Anchor = Anchor.TopLeft;
                        f.Position = caughtFruit.ToSpaceOfOtherDrawable(f.DrawPosition, ExplodingFruitTarget);

                        caughtFruit.Remove(f);

                        ExplodingFruitTarget.Add(f);
                    }

                    f.MoveToY(f.Y + 75, 750, Easing.InSine);
                    f.FadeOut(750);
                    f.Expire();
                }
            }

            /// <summary>
            /// Explode any fruit off the plate.
            /// </summary>
            public void Explode()
            {
                var fruit = caughtFruit.ToArray();

                foreach (var f in fruit)
                {
                    var originalX = f.X * Scale.X;

                    if (ExplodingFruitTarget != null)
                    {
                        f.Anchor = Anchor.TopLeft;
                        f.Position = caughtFruit.ToSpaceOfOtherDrawable(f.DrawPosition, ExplodingFruitTarget);

                        caughtFruit.Remove(f);

                        ExplodingFruitTarget.Add(f);
                    }

                    f.MoveToY(f.Y - 50, 250, Easing.OutSine)
                     .Then()
                     .MoveToY(f.Y + 50, 500, Easing.InSine);

                    f.MoveToX(f.X + originalX * 6, 1000);
                    f.FadeOut(750);

                    f.Expire();
                }
            }
        }
    }
}
