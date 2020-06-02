namespace StockSharp.Algo
{
	using System;
	using System.Collections.Generic;
	using System.Linq;

	using Ecng.Collections;
	using Ecng.Common;

	using StockSharp.Localization;
	using StockSharp.Logging;
	using StockSharp.Messages;

	/// <summary>
	/// Subscription counter adapter.
	/// </summary>
	public class SubscriptionMessageAdapter : MessageAdapterWrapper
	{
		private class SubscriptionInfo
		{
			public ISubscriptionMessage Subscription { get; }

			public SubscriptionInfo(ISubscriptionMessage subscription)
			{
				Subscription = subscription ?? throw new ArgumentNullException(nameof(subscription));
			}

			public SubscriptionStates State { get; set; } = SubscriptionStates.Stopped;

			public override string ToString() => Subscription.ToString();
		}

		private readonly SyncObject _sync = new SyncObject();

		private readonly Dictionary<long, ISubscriptionMessage> _historicalRequests = new Dictionary<long, ISubscriptionMessage>();
		private readonly Dictionary<long, SubscriptionInfo> _subscriptionsById = new Dictionary<long, SubscriptionInfo>();
		private readonly Dictionary<long, long> _replaceId = new Dictionary<long, long>();

		/// <summary>
		/// Initializes a new instance of the <see cref="SubscriptionMessageAdapter"/>.
		/// </summary>
		/// <param name="innerAdapter">Inner message adapter.</param>
		public SubscriptionMessageAdapter(IMessageAdapter innerAdapter)
			: base(innerAdapter)
		{
		}

		/// <summary>
		/// Restore subscription on reconnect.
		/// </summary>
		/// <remarks>
		/// Error case like connection lost etc.
		/// </remarks>
		public bool IsRestoreSubscriptionOnErrorReconnect { get; set; }

		/// <inheritdoc />
		protected override bool OnSendInMessage(Message message)
		{
			switch (message.Type)
			{
				case MessageTypes.Reset:
					return ProcessReset(message);

				case MessageTypes.OrderStatus:
					return ProcessOrderStatusMessage((OrderStatusMessage)message);

				default:
				{
					if (message is ISubscriptionMessage subscrMsg)
						return ProcessInSubscriptionMessage(subscrMsg);
					else
						return base.OnSendInMessage(message);
				}
			}
		}

		private bool ProcessReset(Message message)
		{
			lock (_sync)
			{
				_historicalRequests.Clear();
				_subscriptionsById.Clear();
				_replaceId.Clear();
			}

			return base.OnSendInMessage(message);
		}

		private void ChangeState(SubscriptionInfo info, SubscriptionStates state)
		{
			info.State = info.State.ChangeSubscriptionState(state, info.Subscription.TransactionId, this);
		}

		/// <inheritdoc />
		protected override void OnInnerAdapterNewOutMessage(Message message)
		{
			long TryReplaceOriginId(long id)
			{
				if (id == 0)
					return 0;

				lock (_sync)
					return _replaceId.TryGetValue(id, out var prevId) ? prevId : id;
			}

			var prevOriginId = 0L;
			var newOriginId = 0L;

			if (message is IOriginalTransactionIdMessage originIdMsg1)
			{
				newOriginId = originIdMsg1.OriginalTransactionId;
				prevOriginId = originIdMsg1.OriginalTransactionId = TryReplaceOriginId(newOriginId);
			}

			switch (message.Type)
			{
				case MessageTypes.SubscriptionResponse:
				{
					lock (_sync)
					{
						if (((SubscriptionResponseMessage)message).IsOk())
						{
							if (_subscriptionsById.TryGetValue(prevOriginId, out var info))
							{
								// no need send response after re-subscribe cause response was handled prev time
								if (_replaceId.ContainsKey(newOriginId))
								{
									if (info.State != SubscriptionStates.Stopped)
										return;
								}
								else
									ChangeState(info, SubscriptionStates.Active);
							}
						}
						else
						{
							if (!_historicalRequests.Remove(prevOriginId))
							{
								if (_subscriptionsById.TryGetAndRemove(prevOriginId, out var info))
								{
									ChangeState(info, SubscriptionStates.Error);

									_replaceId.Remove(newOriginId);
								}
							}
						}
					}

					break;
				}

				case MessageTypes.SubscriptionOnline:
				{
					lock (_sync)
					{
						if (!_subscriptionsById.TryGetValue(prevOriginId, out var info))
							break;

						if (_replaceId.ContainsKey(newOriginId))
						{
							// no need send response after re-subscribe cause response was handled prev time

							if (info.State == SubscriptionStates.Online)
								return;
						}
						else
							ChangeState(info, SubscriptionStates.Online);
					}

					break;
				}

				case MessageTypes.SubscriptionFinished:
				{
					lock (_sync)
					{
						if (_replaceId.ContainsKey(newOriginId))
							return;

						_historicalRequests.Remove(prevOriginId);
					}
					
					break;
				}

				default:
				{
					if (message is ISubscriptionIdMessage subscrMsg)
					{
						lock (_sync)
						{
							if (subscrMsg.GetSubscriptionIds().Length == 0)
							{
								if (subscrMsg.OriginalTransactionId != 0 && _historicalRequests.ContainsKey(subscrMsg.OriginalTransactionId))
									subscrMsg.SetSubscriptionIds(subscriptionId: subscrMsg.OriginalTransactionId);
							}
							else
							{
								lock (_sync)
								{
									if (_replaceId.Count > 0)
										subscrMsg.SetSubscriptionIds(subscrMsg.GetSubscriptionIds().Select(id => _replaceId.TryGetValue2(id) ?? id).ToArray());
								}
							}
						}
					}

					break;
				}
			}

			base.OnInnerAdapterNewOutMessage(message);

			switch (message.Type)
			{
				case ExtendedMessageTypes.ReconnectingFinished:
				{
					Message[] subscriptions;

					lock (_sync)
					{
						_replaceId.Clear();

						subscriptions = _subscriptionsById.Values.Distinct().Select(i =>
						{
							var subscription = i.Subscription.TypedClone();
							subscription.TransactionId = TransactionIdGenerator.GetNextId();

							_replaceId.Add(subscription.TransactionId, i.Subscription.TransactionId);

							this.AddInfoLog("Re-map subscription: {0}->{1} for '{2}'.", i.Subscription.TransactionId, subscription.TransactionId, i.Subscription);

							return ((Message)subscription).LoopBack(this);
						}).ToArray();
					}

					foreach (var subscription in subscriptions)
						base.OnInnerAdapterNewOutMessage(subscription);

					break;
				}
			}
		}

		private bool ProcessOrderStatusMessage(OrderStatusMessage message)
		{
			if (message.HasOrderId())
				return base.OnSendInMessage(message);

			return ProcessInSubscriptionMessage(message);
		}

		private bool ProcessInSubscriptionMessage(ISubscriptionMessage message)
		{
			return ProcessInSubscriptionMessage(message, message.DataType);
		}

		private bool ProcessInSubscriptionMessage(ISubscriptionMessage message, DataType dataType)
		{
			if (message == null)
				throw new ArgumentNullException(nameof(message));

			if (dataType == null)
				throw new ArgumentNullException(nameof(dataType));

			var transId = message.TransactionId;

			var isSubscribe = message.IsSubscribe;

			ISubscriptionMessage sendInMsg = null;
			Message[] sendOutMsgs = null;

			lock (_sync)
			{
				if (isSubscribe)
				{
					if (_replaceId.ContainsKey(transId))
					{
						sendInMsg = message;
					}
					else
					{
						var clone = message.TypedClone();

						if (message.To != null)
							_historicalRequests.Add(transId, clone);
						else
							_subscriptionsById.Add(transId, new SubscriptionInfo(clone));

						sendInMsg = message;
					}
				}
				else
				{
					ISubscriptionMessage MakeUnsubscribe(ISubscriptionMessage m)
					{
						m = m.TypedClone();

						m.IsSubscribe = false;
						m.OriginalTransactionId = m.TransactionId;
						m.TransactionId = transId;

						return m;
					}

					var originId = message.OriginalTransactionId;

					if (_historicalRequests.TryGetAndRemove(originId, out var subscription))
					{
						sendInMsg = MakeUnsubscribe(subscription);
					}
					else if (_subscriptionsById.TryGetValue(originId, out var info))
					{
						if (info.State.IsActive())
						{
							// copy full subscription's details into unsubscribe request
							sendInMsg = MakeUnsubscribe(info.Subscription);
						}
						else
							this.AddWarningLog(LocalizedStrings.SubscriptionInState, originId, info.State);
					}
					else
					{
						sendOutMsgs = new[]
						{
							(Message)originId.CreateSubscriptionResponse(new InvalidOperationException(LocalizedStrings.SubscriptionNonExist.Put(originId)))
						};
					}
				}
			}

			var retVal = true;

			if (sendInMsg != null)
			{
				this.AddInfoLog("In: {0}", sendInMsg);
				retVal = base.OnSendInMessage((Message)sendInMsg);
			}

			if (sendOutMsgs != null)
			{
				foreach (var sendOutMsg in sendOutMsgs)
				{
					this.AddInfoLog("Out: {0}", sendOutMsg);
					RaiseNewOutMessage(sendOutMsg);	
				}
			}

			return retVal;
		}

		/// <summary>
		/// Create a copy of <see cref="SubscriptionMessageAdapter"/>.
		/// </summary>
		/// <returns>Copy.</returns>
		public override IMessageChannel Clone()
		{
			return new SubscriptionMessageAdapter(InnerAdapter.TypedClone())
			{
				IsRestoreSubscriptionOnErrorReconnect = IsRestoreSubscriptionOnErrorReconnect,
			};
		}
	}
}