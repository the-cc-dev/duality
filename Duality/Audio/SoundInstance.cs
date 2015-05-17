﻿using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Threading;

using OpenTK;
using OpenTK.Audio.OpenAL;

using Duality.Resources;
using Duality.Backend;

namespace Duality.Audio
{
	/// <summary>
	/// An instance of a <see cref="Duality.Resources.Sound"/>.
	/// </summary>
	[DontSerialize]
	public sealed class SoundInstance : IDisposable
	{
		[FlagsAttribute]
		private enum DirtyFlag : uint
		{
			None		= 0x00000000,

			Pos			= 0x00000001,
			Vel			= 0x00000002,
			Pitch		= 0x00000004,
			Loop		= 0x00000008,
			MaxDist		= 0x00000010,
			RefDist		= 0x00000020,
			Relative	= 0x00000040,
			Vol			= 0x00000080,
			Paused		= 0x00000100,

			AttachedTo	= Pos | Vel,

			All			= 0xFFFFFFFF
		}
		private enum StopRequest
		{
			None,
			EndOfStream,
			Immediately
		}

		public const int AlSource_NotAvailable = 0;
		public const int PriorityStealThreshold = 15;
		public const int PriorityStealLoopedThreshold = 30;


		private	ContentRef<Sound>		sound		= null;
		private	ContentRef<AudioData>	audioData	= null;
		private	INativeAudioSource		native		= null;
		private	bool			disposed		= false;
		private	bool			notYetAssigned	= true;
		private	GameObject		attachedTo		= null;
		private	Vector3			pos				= Vector3.Zero;
		private	Vector3			vel				= Vector3.Zero;
		private	float			vol				= 1.0f;
		private	float			pitch			= 1.0f;
		private	float			panning			= 0.0f;
		private	bool			is3D			= false;
		private	bool			looped			= false;
		private	bool			paused			= false;
		private	bool			registered		= false;
		private	int				curPriority		= 0;
		private	DirtyFlag		dirtyState		= DirtyFlag.All;
		private	float			playTime		= 0.0f;

		// Fading
		private	float			curFade			= 1.0f;
		private	float			fadeTarget		= 1.0f;
		private	float			fadeTimeSec		= 1.0f;
		private	float			pauseFade		= 1.0f;
		private	float			fadeWaitEnd		= 0.0f;

		// Streaming
		private	bool				isStreamed		= false;
		private	VorbisStreamHandle	strOvStr		= null;
		private	int[]				strAlBuffers	= null;
		private	StopRequest			strStopReq		= StopRequest.None;
		private	object				strLock			= new object();
		

		/// <summary>
		/// [GET] The currently used native audio source, as provided by the Duality backend. Don't use this unless you know exactly what you're doing.
		/// </summary>
		public INativeAudioSource Native
		{
			get { return this.native; }
		}
		/// <summary>
		/// [GET] Whether the SoundInstance has been disposed. Disposed objects are not to be
		/// used anymore and should be treated as null or similar.
		/// </summary>
		public bool Disposed
		{
			get { return this.disposed; }
		}
		/// <summary>
		/// [GET] A reference to the <see cref="Duality.Resources.Sound"/> that is being played by
		/// this SoundInstance.
		/// </summary>
		public ContentRef<Sound> Sound
		{
			get { return this.sound; }
		}
		/// <summary>
		/// [GET] A reference to the <see cref="Duality.Resources.AudioData"/> that is being played by
		/// this SoundInstance.
		/// </summary>
		public ContentRef<AudioData> AudioData
		{
			get { return this.audioData; }
		}
		/// <summary>
		/// [GET] The <see cref="GameObject"/> that this SoundInstance is attached to.
		/// </summary>
		public GameObject AttachedTo
		{
			get { return this.attachedTo; }
		}
		/// <summary>
		/// [GET] Whether the sound is played 3d, "in space" or not.
		/// </summary>
		public bool Is3D
		{
			get { return this.is3D; }
		}
		/// <summary>
		/// [GET] The SoundInstances priority.
		/// </summary>
		public int Priority
		{
			get { return this.curPriority; }
		}
		/// <summary>
		/// [GET] When fading in or out, this value represents the current fading state.
		/// </summary>
		public float CurrentFade
		{
			get { return this.curFade; }
		}
		/// <summary>
		/// [GET] The target value for the current fade. Usually 0.0f or 1.0f for fadint out / in.
		/// </summary>
		public float FadeTarget
		{
			get { return this.fadeTarget; }
		}
		/// <summary>
		/// [GET] The time in seconds that this SoundInstance has been playing its sound.
		/// This value is affected by the sounds <see cref="Pitch"/>.
		/// </summary>
		public float PlayTime
		{
			get { return this.playTime; }
		}

		/// <summary>
		/// [GET / SET] The sounds local volume factor.
		/// </summary>
		public float Volume
		{
			get { return this.vol; }
			set { this.vol = value; this.dirtyState |= DirtyFlag.Vol; }
		}
		/// <summary>
		/// [GET / SET] The sounds local pitch factor.
		/// </summary>
		public float Pitch
		{
			get { return this.pitch; }
			set { this.pitch = value; this.dirtyState |= DirtyFlag.Pitch; }
		}
		/// <summary>
		/// [GET / SET] The sounds local stereo panning, ranging from -1.0f (left) to 1.0f (right).
		/// Only available for 2D sounds.
		/// </summary>
		public float Panning
		{
			get { return this.panning; }
			set { this.panning = value; this.dirtyState |= DirtyFlag.Pos; }
		}
		/// <summary>
		/// [GET / SET] Whether the sound is played in a loop.
		/// </summary>
		public bool Looped
		{
			get { return this.looped; }
			set { this.looped = value; this.dirtyState |= DirtyFlag.Loop; }
		}
		/// <summary>
		/// [GET / SET] Whether the sound is currently paused.
		/// </summary>
		public bool Paused
		{
			get { return this.paused; }
			set { this.paused = value; this.dirtyState |= DirtyFlag.Paused; }
		}
		/// <summary>
		/// [GET / SET] The sounds position in space. If it is <see cref="AttachedTo">attached</see> to a GameObject,
		/// this value is considered relative to it.
		/// </summary>
		public Vector3 Pos
		{
			get { return this.pos; }
			set { this.pos = value; }
		}
		/// <summary>
		/// [GET / SET] The sounds velocity. If it is <see cref="AttachedTo">attached</see> to a GameObject,
		/// this value is considered relative to it.
		/// </summary>
		public Vector3 Vel
		{
			get { return this.vel; }
			set { this.vel = value; }
		}


		~SoundInstance()
		{
			this.Dispose(false);
		}
		public void Dispose()
		{
			this.Dispose(true);
			GC.SuppressFinalize(this);
		}
		private void Dispose(bool manually)
		{
			if (!this.disposed)
			{
				this.disposed = true;
				this.OnDisposed(manually);
			}
		}
		private void OnDisposed(bool manually)
		{
			if (manually)
			{
				if (this.isStreamed)
				{
					lock (this.strLock)
					{
						OggVorbis.EndStream(ref this.strOvStr);
					}
					this.strStopReq = StopRequest.Immediately;
				}

				this.attachedTo = null;
				this.curPriority = -1;

				lock (this.strLock)
				{
					if (this.native != null)
					{
						this.native.Dispose();
						this.native = null;
					}
					if (this.strAlBuffers != null)
					{
						for (int i = 0; i < this.strAlBuffers.Length; i++)
						{
							if (!AL.IsBuffer(this.strAlBuffers[i])) return;
							AL.DeleteBuffer(this.strAlBuffers[i]);
						}
						this.strAlBuffers = null;
					}
					this.UnregisterPlaying();
				}
			}
		}

		
		internal SoundInstance(ContentRef<Sound> sound, GameObject attachObj)
		{
			this.attachedTo = attachObj;
			this.is3D = true;
			this.sound = sound;
			this.audioData = this.sound.IsAvailable ? this.sound.Res.FetchData() : null;
		}
		internal SoundInstance(ContentRef<Sound> sound, Vector3 pos)
		{
			this.pos = pos;
			this.is3D = true;
			this.sound = sound;
			this.audioData = this.sound.IsAvailable ? this.sound.Res.FetchData() : null;
		}
		internal SoundInstance(ContentRef<Sound> sound)
		{
			this.sound = sound;
			this.is3D = false;
			this.audioData = this.sound.IsAvailable ? this.sound.Res.FetchData() : null;
		}

		/// <summary>
		/// Stops the sound immediately.
		/// </summary>
		public void Stop()
		{
			this.strStopReq = StopRequest.Immediately;
			if (this.native != null)
				this.native.Stop();
		}
		/// <summary>
		/// Fades the sound to a specific target value.
		/// </summary>
		/// <param name="target">The target value to fade to.</param>
		/// <param name="timeSeconds">The time in seconds the fading will take.</param>
		public void FadeTo(float target, float timeSeconds)
		{
			this.fadeTarget = target;
			this.fadeTimeSec = timeSeconds;
		}
		/// <summary>
		/// Resets the sounds current fade value to zero and starts to fade it in.
		/// </summary>
		/// <param name="timeSeconds">The time in seconds the fading will take.</param>
		public void BeginFadeIn(float timeSeconds)
		{
			this.curFade = 0.0f;
			this.FadeTo(1.0f, timeSeconds);
		}
		/// <summary>
		/// Fades the sound in from its current fade value. Note that SoundInstances are
		/// initialized with a fade value of 1.0f because they aren't faded in generally. 
		/// To achieve a regular "fade in" effect, you should use <see cref="BeginFadeIn(float)"/>.
		/// </summary>
		/// <param name="timeSeconds">The time in seconds the fading will take.</param>
		public void FadeIn(float timeSeconds)
		{
			this.FadeTo(1.0f, timeSeconds);
		}
		/// <summary>
		/// Fades out the sound.
		/// </summary>
		/// <param name="timeSeconds">The time in seconds the fading will take.</param>
		public void FadeOut(float timeSeconds)
		{
			this.FadeTo(0.0f, timeSeconds);
		}
		/// <summary>
		/// Halts the current fading, keepinf the current fade value as fade target.
		/// </summary>
		public void StopFade()
		{
			this.fadeTarget = this.curFade;
		}

		private bool GrabAlSource()
		{
			if (this.native != null) return true;

			// Retrieve maximum number of sources by kind (2D / 3D)
			int maxNum = DualityApp.Sound.MaxOpenALSources * 3 / 4;
			if (!this.is3D) maxNum = DualityApp.Sound.MaxOpenALSources - maxNum;
			// Determine how many sources of this kind (2D / 3D) are playing
			int curNum = this.is3D ? DualityApp.Sound.NumPlaying3D : DualityApp.Sound.NumPlaying2D;
			// Determine how many sources using this sound resource are playing
			int curNumSoundRes = DualityApp.Sound.GetNumPlaying(this.sound);

			if (DualityApp.Sound.NumAvailable > 0 &&
				curNum < maxNum &&
				curNumSoundRes < this.sound.Res.MaxInstances)
			{
				this.native = DualityApp.AudioBackend.CreateSource();
			}
			else
			{
				bool searchSimilar = curNumSoundRes >= this.sound.Res.MaxInstances;
				lock (this.strLock)
				{
					this.curPriority = this.PreCalcPriority();

					foreach (SoundInstance inst in DualityApp.Sound.Playing)
					{
						if (inst.native == null) continue;
						if (!searchSimilar && this.is3D != inst.is3D) continue;
						if (searchSimilar && this.sound.Res != inst.sound.Res) continue;
						
						float ownPrioMult = 1.0f;
						if (searchSimilar && !inst.Looped) ownPrioMult *= MathF.Sqrt(inst.playTime + 1.0f);
							
						if (this.curPriority * ownPrioMult > inst.curPriority + 
							(inst.Looped ? PriorityStealLoopedThreshold : PriorityStealThreshold))
						{
							lock (inst.strLock)
							{
								this.native = inst.native;
								this.native.Reset();
								inst.native = null;
							}
							break;
						}
						// List sorted by priority - if first fails, all will. Exception: Searching
						// similar sounds where play times are taken into account
						if (!searchSimilar)
							break;
					}

				}
			}

			this.notYetAssigned = false;
			return this.native != null;
		}
		private int PreCalcPriority()
		{
			// Don't take fade into account: If a yet-to-fade-in sound wants to grab
			// the source of a already-playing sound, it should get its chance.
			float volTemp = this.GetTypeVolFactor() * this.sound.Res.VolumeFactor * this.vol;
			float priorityTemp = 1000.0f;
			priorityTemp *= volTemp;

			if (this.is3D)
			{
				float minDistTemp = this.sound.Res.MinDist;
				float maxDistTemp = this.sound.Res.MaxDist;
				Vector3 listenerPos = DualityApp.Sound.ListenerPos;
				Vector3 posTemp;
				if (this.attachedTo != null)	posTemp = this.attachedTo.Transform.Pos + this.pos;
				else							posTemp = this.pos;
				float dist = MathF.Sqrt(
					(posTemp.X - listenerPos.X) * (posTemp.X - listenerPos.X) +
					(posTemp.Y - listenerPos.Y) * (posTemp.Y - listenerPos.Y) +
					(posTemp.Z - listenerPos.Z) * (posTemp.Z - listenerPos.Z) * 0.25f);
				priorityTemp *= Math.Max(0.0f, 1.0f - (dist - minDistTemp) / (maxDistTemp - minDistTemp));
			}

			int numPlaying = DualityApp.Sound.GetNumPlaying(this.sound);
			return (int)Math.Round(priorityTemp / Math.Sqrt(numPlaying));
		}
		private float GetTypeVolFactor()
		{
			float optVolFactor;
			switch (this.sound.IsAvailable ? this.sound.Res.Type : SoundType.World)
			{
				case SoundType.UserInterface:
					optVolFactor = DualityApp.UserData.SfxEffectVol;
					break;
				case SoundType.World:
					optVolFactor = DualityApp.UserData.SfxEffectVol;
					break;
				case SoundType.Speech:
					optVolFactor = DualityApp.UserData.SfxSpeechVol;
					break;
				case SoundType.Music:
					optVolFactor = DualityApp.UserData.SfxMusicVol;
					break;
				default:
					optVolFactor = 1.0f;
					break;
			}
			return optVolFactor * DualityApp.UserData.SfxMasterVol * 0.5f;
		}
		private void RegisterPlaying()
		{
			if (this.registered) return;
			DualityApp.Sound.RegisterPlaying(this.sound, this.is3D);
			this.registered = true;
		}
		private void UnregisterPlaying()
		{
			if (!this.registered) return;
			DualityApp.Sound.UnregisterPlaying(this.sound, this.is3D);
			this.registered = false;
		}

		/// <summary>
		/// Updates the SoundInstance
		/// </summary>
		public void Update()
		{
			lock (this.strLock)
			{
				// Check existence of attachTo object
				if (this.attachedTo != null && this.attachedTo.Disposed) this.attachedTo = null;

				// Retrieve sound resource values
				Sound soundRes = this.sound.Res;
				AudioData audioDataRes = this.audioData.Res;
				if (soundRes == null || audioDataRes == null)
				{
					this.Dispose();
					return;
				}
				float optVolFactor = this.GetTypeVolFactor();
				float minDistTemp = soundRes.MinDist;
				float maxDistTemp = soundRes.MaxDist;
				float volTemp = optVolFactor * soundRes.VolumeFactor * this.vol * this.curFade * this.pauseFade;
				float pitchTemp = soundRes.PitchFactor * this.pitch;
				float priorityTemp = 1000.0f;
				priorityTemp *= volTemp;

				// Calculate 3D source values, distance and priority
				Vector3 posAbs = this.pos;
				Vector3 velAbs = this.vel;
				if (this.is3D)
				{
					Components.Transform attachTransform = this.attachedTo != null ? this.attachedTo.Transform : null;

					// Attach to object
					if (this.attachedTo != null && this.attachedTo != DualityApp.Sound.Listener)
					{
						MathF.TransformCoord(ref posAbs.X, ref posAbs.Y, attachTransform.Angle);
						MathF.TransformCoord(ref velAbs.X, ref velAbs.Y, attachTransform.Angle);
						posAbs += attachTransform.Pos;
						velAbs += attachTransform.Vel;
					}

					// Distance check
					Vector3 listenerPos = DualityApp.Sound.ListenerPos;
					float dist;
					if (this.attachedTo != DualityApp.Sound.Listener)
						dist = MathF.Sqrt(
							(posAbs.X - listenerPos.X) * (posAbs.X - listenerPos.X) +
							(posAbs.Y - listenerPos.Y) * (posAbs.Y - listenerPos.Y) +
							(posAbs.Z - listenerPos.Z) * (posAbs.Z - listenerPos.Z) * 0.25f);
					else
						dist = MathF.Sqrt(
							posAbs.X * posAbs.X +
							posAbs.Y * posAbs.Y +
							posAbs.Z * posAbs.Z * 0.25f);
					if (dist > maxDistTemp)
					{
						this.Dispose();
						return;
					}
					else
						priorityTemp *= Math.Max(0.0f, 1.0f - (dist - minDistTemp) / (maxDistTemp - minDistTemp));
				}

				// Grab an OpenAL source, if not yet assigned
				if (this.notYetAssigned)
				{
					if (this.GrabAlSource())
					{
						this.RegisterPlaying();
					}
					else
					{
						this.Dispose();
						return;
					}
				}

				// Determine source state, if available
				ALSourceState stateTemp = ALSourceState.Stopped;
				if (this.native != null) stateTemp = AL.GetSourceState((this.native as Backend.DefaultOpenTK.NativeAudioSource).Handle);

				// If the source is stopped / finished, dispose and return
				if (stateTemp == ALSourceState.Stopped && (!audioDataRes.IsStreamed || this.strStopReq != StopRequest.None))
				{
					this.Dispose();
					return;
				}
				else if (stateTemp == ALSourceState.Initial && this.strStopReq == StopRequest.Immediately)
				{
					this.Dispose();
					return;
				} 

				// Fading in and out
				bool fadeOut = this.fadeTarget <= 0.0f;
				if (!this.paused)
				{
					if (this.fadeTarget != this.curFade)
					{
						float fadeTemp = Time.TimeMult * Time.SPFMult / Math.Max(0.05f, this.fadeTimeSec);

						if (this.fadeTarget > this.curFade)
							this.curFade += fadeTemp;
						else
							this.curFade -= fadeTemp;

						if (Math.Abs(this.curFade - this.fadeTarget) < fadeTemp * 2.0f)
							this.curFade = this.fadeTarget;

						this.dirtyState |= DirtyFlag.Vol;
					}
				}

				// Special paused-fading
				if (this.paused && this.pauseFade > 0.0f)
				{
					this.pauseFade = MathF.Max(0.0f, this.pauseFade - Time.TimeMult * Time.SPFMult * 5.0f);
					this.dirtyState |= DirtyFlag.Paused | DirtyFlag.Vol;
				}
				else if (!this.paused && this.pauseFade < 1.0f)
				{
					this.pauseFade = MathF.Min(1.0f, this.pauseFade + Time.TimeMult * Time.SPFMult * 5.0f);
					this.dirtyState |= DirtyFlag.Paused | DirtyFlag.Vol;
				}

				// Hack: Volume always dirty - just to be sure
				this.dirtyState |= DirtyFlag.Vol;

				if (this.native != null)
				{
					int handle = (this.native as Backend.DefaultOpenTK.NativeAudioSource).Handle;
					if (this.is3D)
					{
						// Hack: Relative always dirty to support switching listeners without establishing a notifier-event
						this.dirtyState |= DirtyFlag.Relative;
						if (this.attachedTo != null) this.dirtyState |= DirtyFlag.AttachedTo;

						if ((this.dirtyState & DirtyFlag.Relative) != DirtyFlag.None)
							AL.Source(handle, ALSourceb.SourceRelative, this.attachedTo == DualityApp.Sound.Listener);
						if ((this.dirtyState & DirtyFlag.Pos) != DirtyFlag.None)
							AL.Source(handle, ALSource3f.Position, posAbs.X, -posAbs.Y, -posAbs.Z * 0.5f);
						if ((this.dirtyState & DirtyFlag.Vel) != DirtyFlag.None)
							AL.Source(handle, ALSource3f.Velocity, velAbs.X, -velAbs.Y, -velAbs.Z);
					}
					else
					{
						if ((this.dirtyState & DirtyFlag.Relative) != DirtyFlag.None)
							AL.Source(handle, ALSourceb.SourceRelative, true);
						if ((this.dirtyState & DirtyFlag.Pos) != DirtyFlag.None)
							AL.Source(handle, ALSource3f.Position, this.panning, 0.0f, 0.0f);
						if ((this.dirtyState & DirtyFlag.Vel) != DirtyFlag.None)
							AL.Source(handle, ALSource3f.Velocity, 0.0f, 0.0f, 0.0f);
					}
					if ((this.dirtyState & DirtyFlag.MaxDist) != DirtyFlag.None)
						AL.Source(handle, ALSourcef.MaxDistance, maxDistTemp);
					if ((this.dirtyState & DirtyFlag.RefDist) != DirtyFlag.None)
						AL.Source(handle, ALSourcef.ReferenceDistance, minDistTemp);
					if ((this.dirtyState & DirtyFlag.Loop) != DirtyFlag.None)
						AL.Source(handle, ALSourceb.Looping, (this.looped && !audioDataRes.IsStreamed));
					if ((this.dirtyState & DirtyFlag.Vol) != DirtyFlag.None)
						AL.Source(handle, ALSourcef.Gain, volTemp);
					if ((this.dirtyState & DirtyFlag.Pitch) != DirtyFlag.None)
						AL.Source(handle, ALSourcef.Pitch, pitchTemp);
					if ((this.dirtyState & DirtyFlag.Paused) != DirtyFlag.None)
					{
						if (this.paused && this.pauseFade == 0.0f && stateTemp == ALSourceState.Playing)
							AL.SourcePause(handle);
						else if ((!this.paused || this.pauseFade > 0.0f) && stateTemp == ALSourceState.Paused)
							AL.SourcePlay(handle);
					}
				}
				this.dirtyState = DirtyFlag.None;

				// Update play time
				if (!this.paused)
				{
					this.playTime += MathF.Max(0.5f, pitchTemp) * Time.TimeMult * Time.SPFMult;
					if (this.sound.Res.FadeOutAt > 0.0f && this.playTime >= this.sound.Res.FadeOutAt)
						this.FadeOut(this.sound.Res.FadeOutTime);
				}

				// Finish priority calculation
				this.curPriority = (int)Math.Round(priorityTemp / Math.Sqrt(DualityApp.Sound.GetNumPlaying(this.sound))); 

				// Initially play the source
				if (stateTemp == ALSourceState.Initial && !this.paused)
				{
					if (audioDataRes.IsStreamed)
					{
						this.isStreamed = true;
						DualityApp.Sound.EnqueueForStreaming(this);
					}
					else if (audioDataRes.Native != null)
					{
						int handle = (this.native as Backend.DefaultOpenTK.NativeAudioSource).Handle;
						AL.SourceQueueBuffer(handle, (audioDataRes.Native as Backend.DefaultOpenTK.NativeAudioBuffer).Handle);
						AL.SourcePlay(handle);
					} 
				}
				
				// Remove faded out sources
				if (fadeOut && volTemp <= 0.0f)
				{
					this.fadeWaitEnd += Time.TimeMult * Time.MsPFMult;
					// After fading out entirely, wait 50 ms before actually stopping the source to prevent unpleasant audio tick / glitch noises
					if (this.fadeWaitEnd > 50.0f)
					{
						this.Dispose();
						return;
					}
				}
				else
					this.fadeWaitEnd = 0.0f;

			}

		}


		internal bool PerformStreaming()
		{
			lock (this.strLock)
			{
				if (this.Disposed) return false;

				ALSourceState stateTemp = ALSourceState.Stopped;
				bool sourceAvailable = this.native != null;
				int handle = this.native != null ? (this.native as Backend.DefaultOpenTK.NativeAudioSource).Handle : 0;
				if (sourceAvailable) stateTemp = AL.GetSourceState(handle);

				if (stateTemp == ALSourceState.Stopped && this.strStopReq != StopRequest.None)
				{
					// Stopped due to regular EOF. If strStopReq is NOT set,
					// the source stopped playing because it reached the end of the buffer
					// but in fact only because we were too slow inserting new data.
					return false;
				}
				else if (this.strStopReq == StopRequest.Immediately)
				{
					// Stopped intentionally due to Stop()
					if (sourceAvailable) AL.SourceStop(handle);
					return false;
				}

				AudioData audioDataRes = this.audioData.Res;
				if (!this.sound.IsAvailable || audioDataRes == null)
				{
					this.Dispose();
					return false;
				}

				if (sourceAvailable)
				{
					if (stateTemp == ALSourceState.Initial)
					{
						// Initialize streaming
						PerformStreamingBegin(audioDataRes);

						// Initially play source
						AL.SourcePlay(handle);
						stateTemp = AL.GetSourceState(handle);
					}
					else
					{
						// Stream new data
						PerformStreamingUpdate(audioDataRes);

						// If the source stopped unintentionally, restart it. (See above)
						if (stateTemp == ALSourceState.Stopped && this.strStopReq == StopRequest.None)
						{
							AL.SourcePlay(handle);
						}
					}
				}
			}

			return true;
		}
		private void PerformStreamingBegin(AudioData audioDataRes)
		{
			int handle = (this.native as Backend.DefaultOpenTK.NativeAudioSource).Handle;

			// Generate streaming buffers
			this.strAlBuffers = new int[3];
			for (int i = 0; i < this.strAlBuffers.Length; ++i)
			{
				AL.GenBuffers(1, out this.strAlBuffers[i]);
			}

			// Begin streaming
			OggVorbis.BeginStreamFromMemory(audioDataRes.OggVorbisData, out this.strOvStr);

			// Initially, completely fill all buffers
			for (int i = 0; i < this.strAlBuffers.Length; ++i)
			{
				PcmData pcm;
				bool eof = !OggVorbis.StreamChunk(this.strOvStr, out pcm);
				if (pcm.DataLength > 0)
				{
					AL.BufferData(
						this.strAlBuffers[i], 
						pcm.ChannelCount == 1 ? ALFormat.Mono16 : ALFormat.Stereo16,
						pcm.Data, 
						pcm.DataLength * PcmData.SizeOfDataElement, 
						pcm.SampleRate);
					AL.SourceQueueBuffer(handle, this.strAlBuffers[i]);
					if (eof) break;
				}
				else break;
			}
		}
		private void PerformStreamingUpdate(AudioData audioDataRes)
		{
			int handle = (this.native as Backend.DefaultOpenTK.NativeAudioSource).Handle;

			int num;
			AL.GetSource(handle, ALGetSourcei.BuffersProcessed, out num);
			while (num > 0)
			{
				num--;

				int unqueued;
				unqueued = AL.SourceUnqueueBuffer(handle);

				if (OggVorbis.IsStreamValid(this.strOvStr))
				{
					PcmData pcm;
					bool eof = !OggVorbis.StreamChunk(this.strOvStr, out pcm);
					if (eof)
					{
						OggVorbis.EndStream(ref this.strOvStr);
						if (this.looped)
						{
							OggVorbis.BeginStreamFromMemory(audioDataRes.OggVorbisData, out this.strOvStr);
							if (pcm.DataLength == 0)
								eof = !OggVorbis.StreamChunk(this.strOvStr, out pcm);
							else
								eof = false;
						}
					}
					if (pcm.DataLength > 0)
					{
						AL.BufferData(
							unqueued, 
							pcm.ChannelCount == 1 ? ALFormat.Mono16 : ALFormat.Stereo16,
							pcm.Data, 
							pcm.DataLength * PcmData.SizeOfDataElement, 
							pcm.SampleRate);
						AL.SourceQueueBuffer(handle, unqueued);
					}
					if (pcm.DataLength == 0 || eof)
					{
						this.strStopReq = StopRequest.EndOfStream;
						break;
					}
				}
			}
		}

	}
}
