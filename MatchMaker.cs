using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using Venus.Utilities;
using System.IO;
using Venus.Network;
using UnityEngine;
using Mahjong.Model;
using Mahjong.Network;
using Ourgame.Protocols.Mahjong;
using System.Text;
using SimpleJSON;
using System.Linq;

namespace Mahjong.Core {
    [Serializable]
    public sealed class BeginGameFailedEventArgs : EventArgs {
        public int delayTimeToRetry {
            get; private set;
        }

        public BeginGameFailedEventArgs (int seconds) {
            this.delayTimeToRetry = seconds;
        }
    }

    [Serializable]
    public sealed class AnnouncementArrivedEventArgs : EventArgs {
        public int count {
            get; private set;
        }

        public AnnouncementArrivedEventArgs (int count) {
            this.count = count;
        }
    }

    [Serializable]
    public sealed class InvitationArrivedEventArgs : EventArgs {
        public string sourcePlayerName {
            get; private set;
        }

        public string joinCode {
            get; private set;
        }

        public string url {
            get; private set;
        }

        public gameRule rule {
            get; private set;
        }

        public InvitationArrivedEventArgs (NtcInvite data) {
            sourcePlayerName = Encoding.UTF8.GetString (data.inviterName);
            joinCode = Encoding.UTF8.GetString (data.enterCode);
            rule = data.rule;
            url = Encoding.UTF8.GetString (data.inviterUrl);
        }
    }

    [Serializable]
    public sealed class GameDisbandedEventArgs : EventArgs {
        public string senderNickName {
            get; private set;
        }

        public GameDisbandedEventArgs (string senderRoleName) {
            var seatInfo = Game.current.findPlayerByRoleName (senderRoleName);
            if (seatInfo != null) {
                Debug.Assert (seatInfo != null);
                senderNickName = seatInfo.playerInfo.nickName;
            } else {
                senderNickName = senderRoleName;
            }

        }
    }

    public sealed class MatchMaker : MonoBehaviour, IDisposable {
        static private MatchMaker _current = null;

        private long _gameConfigId = 0;
        private List<ChargeCfg> _chargConfigs = new List<ChargeCfg> ();
        private int _currentServerIndex = 0;
        private List<KeyValuePair<string, int>> _serverAddressList = new List<KeyValuePair<string, int>> ();
        private List<KeyValuePair<int, string>> _messageList = new List<KeyValuePair<int, string>> ();
        private OperationQueue _queue;
        private DateTime? _timeWhenPaused = null;
        private float _timer = 0;
        private bool _isStop = false;

        public ObserverHub<EventArgs> serverConnected {
            get; private set;
        }

        public ObserverHub<EventArgs> serverDisconnected {
            get; private set;
        }

        public ObserverHub<EventArgs> serverReady {
            get; private set;
        }

        public ObserverHub<EventArgs> continueGame {
            get; private set;
        }

        public ObserverHub<EventArgs> playersInGameChanged {
            get; private set;
        }

        public ObserverHub<GameDisbandedEventArgs> gameDisbanded {
            get; private set;
        }

        public ObserverHub<EventArgs> gameWillBegin {
            get; private set;
        }

        public ObserverHub<EventArgs> gameBegin {
            get; private set;
        }

        public ObserverHub<EventArgs> newMessageArrived {
            get; private set;
        }

        public ObserverHub<AnnouncementArrivedEventArgs> announcementArrived {
            get; private set;
        }

        public ObserverHub<BeginGameFailedEventArgs> beginGameFailed {
            get; private set;
        }

        public ObserverHub<InvitationArrivedEventArgs> invitationArrived {
            get; private set;
        }

        public ReadOnlyCollection<KeyValuePair<int, string>> messages {
            get {
                return _messageList.AsReadOnly ();
            }
        }

        public IEnumerable<KeyValuePair<int, long>> roundConfig {
            get {
                return from item in _chargConfigs
                       orderby item.roundCount ascending
                       select new KeyValuePair<int, long> (item.roundCount, item.charge);
            }
        }

        public bool isOwner {
            get; private set;
        }

        public bool isConnected {
            get; private set;
        }

        static public MatchMaker current {
            get {
                if (_current == null) {
                    _current = new GameObject ().AddComponent<MatchMaker> ();
                    _current.gameObject.name = "Match Maker";
                    _current.init ();
                }

                return _current;
            }
        }

        #region IDisposable implementation

        public void Dispose () {
            serverConnected.Dispose ();
            serverDisconnected.Dispose ();
            serverReady.Dispose ();
            continueGame.Dispose ();
            playersInGameChanged.Dispose ();
            gameDisbanded.Dispose ();
            gameWillBegin.Dispose ();
            gameBegin.Dispose ();
            newMessageArrived.Dispose ();
            announcementArrived.Dispose ();
            beginGameFailed.Dispose ();
            invitationArrived.Dispose ();

            _queue.Dispose ();

            closeConnect ();
            GameObject.Destroy (gameObject);
        }

        public void closeConnect () {
            ConnectionManager.sharedInstance.connectionBroken.removeObserver (onConnectionBroken);
            ConnectionManager.sharedInstance.closeConection (Config.CONNECTION_NAME_MATCHMAKER);
        }

        #endregion

        private void init () {
            _queue = new OperationQueue ();
            serverConnected = new ObserverHub<EventArgs> (_queue);
            serverDisconnected = new ObserverHub<EventArgs> (_queue);
            serverReady = new ObserverHub<EventArgs> (_queue);
            continueGame = new ObserverHub<EventArgs> (_queue);
            playersInGameChanged = new ObserverHub<EventArgs> (_queue);
            gameDisbanded = new ObserverHub<GameDisbandedEventArgs> (_queue);
            gameWillBegin = new ObserverHub<EventArgs> (_queue);
            gameBegin = new ObserverHub<EventArgs> (_queue);
            newMessageArrived = new ObserverHub<EventArgs> (_queue);
            announcementArrived = new ObserverHub<AnnouncementArrivedEventArgs> (_queue);
            beginGameFailed = new ObserverHub<BeginGameFailedEventArgs> (_queue);
            invitationArrived = new ObserverHub<InvitationArrivedEventArgs> (_queue);

            ConnectionManager.sharedInstance.connectionBroken.addObserver (onConnectionBroken);

            _currentServerIndex = 0;
            updateServerList ();
        }

        void OnApplicationPause (bool isPaused) {
            if (isPaused) {
                _timeWhenPaused = DateTime.Now;

                var connection = ConnectionManager.sharedInstance.connectionNamed (Config.CONNECTION_NAME_MATCHMAKER);
                if (connection != null) {
                    connection.paused = true;
                }
            } else {
                var connection = ConnectionManager.sharedInstance.connectionNamed (Config.CONNECTION_NAME_MATCHMAKER);
                if (connection == null) {
                    _timer = 3;
                    _isStop = true;
                    serverDisconnected.fire (EventArgs.Empty, () => {
                    });
                    return;
                }

                if (!_timeWhenPaused.HasValue) {
                    return;
                }

                if (DateTime.Now.Subtract (_timeWhenPaused.Value).Minutes >= 1) {
                    ConnectionManager.sharedInstance.connectionBroken.removeObserver (onConnectionBroken);
                    ConnectionManager.sharedInstance.closeConection (Config.CONNECTION_NAME_MATCHMAKER);

 
                    ConnectionManager.sharedInstance.connectionBroken.addObserver (onConnectionBroken);
                    reconnect ((isSuccessful, completion) => {
                        if (isSuccessful) {
                            completion ();
                        } else {
                            ConnectionManager.sharedInstance.connectionBroken.removeObserver (onConnectionBroken);
                            ConnectionManager.sharedInstance.closeConection (Config.CONNECTION_NAME_MATCHMAKER);

                            serverDisconnected.fire (EventArgs.Empty);
                        }
                    });
                } else {
                    Dispatcher.main.dispatchAsync (() => {
                        if (Application.internetReachability == NetworkReachability.NotReachable) {
                            ConnectionManager.sharedInstance.closeConection (Config.CONNECTION_NAME_MATCHMAKER);
                            serverDisconnected.fire (EventArgs.Empty, () => {
                            });
                        }
                        if (connection != null) {
                            connection.paused = false;
                            _timeWhenPaused = null;
                        }
                    }, 1);
                }
            }
        }

        public void updateServerList () {
            _serverAddressList.Clear ();

            // #if (UNITY_EDITOR && !QA_TESTING) || MOBILE_TESTING
            //             _serverAddressList.Add (new KeyValuePair<string, int> (Config.SERVER_ADDRESS_MATCH_MAKER.Address.ToString (), Config.SERVER_ADDRESS_MATCH_MAKER.Port));
            // #else
            if (GameConfig.global.matchMakerServerList != null) {
                foreach (var item in GameConfig.global.matchMakerServerList.Childs) {
                    _serverAddressList.Add (new KeyValuePair<string, int> (item ["ip"].Value, item ["port"].AsInt));
                }
                _serverAddressList.Sort ((x, y) => {
                    return UnityEngine.Random.Range (-1, 1);
                });
            }
            // #endif
        }

        public long chargesOfGameByRound (int roundCount) {
            if (_chargConfigs.Count == 0) {
                return -1;
            }

            var item = _chargConfigs.Find ((obj) => obj.roundCount == roundCount);
            if (item == null) {
                return -1;
            }

            return item.charge;
        }

        public void reconnect (Action<bool, Action> completion) {
            if (ConnectionManager.sharedInstance.connectionNamed (Config.CONNECTION_NAME_MATCHMAKER) != null) {
                Dispatcher.main.dispatchAsync (() => {
                    completion (true, () => {
                    });
                }, 1.0f);
                return;
            }

            if (_currentServerIndex == _serverAddressList.Count) {
                Dispatcher.main.dispatchAsync (() => {
                    completion (false, () => {
                        _currentServerIndex = 0;
                    });
                }, 1.0f);
                return;
            }
            var address = _serverAddressList [_currentServerIndex].Key;
            var port = _serverAddressList [_currentServerIndex].Value;

            if (Config.isOverseas) {
                address = "game.baidu.com";
                port = 3000;
            }

            ConnectionManager.sharedInstance.reconnect (Config.CONNECTION_NAME_MATCHMAKER, address, port, (status, connection) => {
                if (status == ConnectionStatus.Broken) {
                    ++_currentServerIndex;
                    reconnect (completion);
                } else if (status == ConnectionStatus.Connected) {
                    isConnected = true;
                    _currentServerIndex = 0;

                    connection.addMessageHandler ((uint) MessageCategory.Ack | (uint) MatchMakerMessageID.NtcTabUser, onPlayerInGameChanged);
                    connection.addMessageHandler ((uint) MessageCategory.Ack | (uint) MatchMakerMessageID.StartGame, onStartGame);
                    connection.addMessageHandler ((uint) MessageCategory.Ack | (uint) MatchMakerMessageID.GameList, onGameListUpdated);
                    connection.addMessageHandler ((uint) MessageCategory.Ack | (uint) MatchMakerMessageID.NtcMsg, onMessage);
                    connection.addMessageHandler ((uint) MessageCategory.Ack | (uint) MatchMakerMessageID.NtcInvite, onInvitationArrived);
                    connection.addMessageHandler ((uint) MessageCategory.Ack | (uint) MatchMakerMessageID.NtcDisbandTab, onDisbandTab);
                    serverConnected.fire (EventArgs.Empty, () => {
                        var info = Account.localPlayer;
                        var data = new reqLogin ();
                        data.nickName = Encoding.UTF8.GetBytes (Account.localPlayer.nickName);
                        data.avatarUrl = Encoding.UTF8.GetBytes (Account.localPlayer.avatarUrl);
                        data.channelId = GameConfig.global.loginStatusData.channelId;
                        data.gameId = GameConfig.global.loginStatusData.gameId;
                        data.ticket = GameConfig.global.loginStatusData.ticket;

                        var request = new Request<OurgameHeader> ((uint) MatchMakerMessageID.LOGIN);
                        request.setData (data);

                        request.responseMessageId = (uint) MessageCategory.Ack | (uint) MatchMakerMessageID.LOGIN;
                        request.responseCallback = (response, responseCompletion) => {
                            var ack = response.read<ackLogin> ();

                            bool isContinueGame = false;
                            if (ack.result == 0) {
                                Account.localPlayer.setUserData (ack);

                                if (ack.enterCode != null) {
                                    GameConfig.global.enterCode = Encoding.UTF8.GetString (ack.enterCode);
                                    isContinueGame = true;
                                }
                            }

                            completion (ack.result == 0, () => {
                                if (isContinueGame) {
                                    continueGame.fire (EventArgs.Empty, responseCompletion);
                                } else {
                                    responseCompletion ();
                                }
                            });
                        };

                        connection.sendRequest (request);
                    });
                }
            });
        }

        public void createGame (int roundCount, gameRule rule, Action<CreateOrJoinGameErrorCode, Action> completion) {

            var connection = ConnectionManager.sharedInstance.connectionNamed (Config.CONNECTION_NAME_MATCHMAKER);
            if (connection == null) {
                completion (CreateOrJoinGameErrorCode.CannotConnectToServer, () => {
                });
            } else {
                rule.PanType = 2;
                var data = new reqCreateGame ();
                data.gameCfgId = (int) _gameConfigId;
                data.rule = rule;
                data.userName = Encoding.Default.GetBytes (Account.localPlayer.userId);
                data.chargeCfgId = (from config in _chargConfigs
                                    where config.roundCount == roundCount
                                    select (int) config.chargeCfgId).FirstOrDefault ();

                if (GameConfig.global.lastGameRoomId != -1) {
                    data.preRoomId = GameConfig.global.lastGameRoomId;
                    GameConfig.global.lastGameRoomId = -1;
                }

                var request = new Request<OurgameHeader> ((int) MatchMakerMessageID.CreateGame);
                request.setData (data);
                request.responseMessageId = (uint) MessageCategory.Ack | (uint) MatchMakerMessageID.CreateGame;
                request.responseCallback = (response, responseCompletion) => {
                    var ack = response.read<ackCreateGame> ();
                    if (ack.result == 0) {
                        GameConfig.global.enterCode = Encoding.Default.GetString (ack.enterCode);
                        responseCompletion ();

                        joinGame (GameConfig.global.enterCode, completion);	
                    } else {
                        completion ((CreateOrJoinGameErrorCode) ack.result, responseCompletion);
                    }
                };

                connection.sendRequest (request);
            }
        }

        public void joinGame (string enterCode, Action<CreateOrJoinGameErrorCode, Action> completion) {
            var connection = ConnectionManager.sharedInstance.connectionNamed (Config.CONNECTION_NAME_MATCHMAKER);
            if (connection == null) {
                completion (CreateOrJoinGameErrorCode.CannotConnectToServer, () => {
                });
            } else {
                if (string.IsNullOrEmpty (enterCode)) {
                    completion (CreateOrJoinGameErrorCode.GameNotExist, () => {
                    });
                    return;
                }
                var data = new reqAddTab ();
                data.enterCode = Encoding.UTF8.GetBytes (enterCode);
                data.userName = Encoding.UTF8.GetBytes (Account.localPlayer.userId);
                if (Account.localPlayer.avatarUrl != null) {
                    data.avatarUrl = Encoding.UTF8.GetBytes (Account.localPlayer.avatarUrl);
                }
                data.roleName = Encoding.UTF8.GetBytes (Account.localPlayer.roleName);
                data.showName = Encoding.UTF8.GetBytes (Account.localPlayer.showName);
                data.nickName = Encoding.UTF8.GetBytes (Account.localPlayer.nickName);
                data.treasure = Account.localPlayer.coin;

                var request = new Request<OurgameHeader> ((int) MatchMakerMessageID.AddTab);
                request.setData (data);
                request.responseMessageId = (uint) MessageCategory.Ack | (uint) MatchMakerMessageID.AddTab;
                request.responseCallback = (response, responseCompletion) => {
                    var ack = response.read<ackAddTab> ();
                    if (ack.result == 0) {
                        GameConfig.global.enterCode = enterCode;
                        GameConfig.global.currentRoomId = ack.roomId;

                        Game.createGame (ack);
                    }

                    completion ((CreateOrJoinGameErrorCode) ack.result, () => {
                        if (ack.result == 0) {
                            Dispatcher.main.dispatchAsync (() => {
                                playersInGameChanged.fire (EventArgs.Empty, () => {
                                });
                            }, 1);
                          
                            if (ack.gameIP != null && ack.gameIP.Length > 0) {

                                Game.current.gameServerAddress = Encoding.Default.GetString (ack.gameIP);
                                Game.current.gameServerPort = ack.gamePort;

                                gameWillBegin.fire (EventArgs.Empty, responseCompletion);
                            } else {

                                responseCompletion ();
                            }
                            
                        } else {

                            GameConfig.global.enterCode = null;
                            responseCompletion ();
                        }
                    });
                };

                connection.sendRequest (request);
            }
        }

        public void leaveGame (bool justBackToLobby, Action<int> completion, bool isGameOver = false) {
            Game.destroyGame (() => {
                if (justBackToLobby) {
                    isOwner = true;
                    completion (0);
                } else {
                    Debug.Assert (GameConfig.global.currentRoomId != -1);
                    isOwner = false;
                    var connection = ConnectionManager.sharedInstance.connectionNamed (Config.CONNECTION_NAME_MATCHMAKER);
                    if (connection == null) {
                        completion (0);
                    } else {
                        var data = new reqLeaveTab ();
                        data.enterCode = Encoding.Default.GetBytes (GameConfig.global.enterCode);
                        data.userName = Encoding.Default.GetBytes (Account.localPlayer.userId);
                        data.roomId = GameConfig.global.currentRoomId;


                        var request = new Request<OurgameHeader> ((int) MatchMakerMessageID.LeaveTab);
                        request.setData (data);
                        request.responseMessageId = (uint) MessageCategory.Ack | (uint) MatchMakerMessageID.LeaveTab;
                        request.responseCallback = (response, responseCompletion) => {
                            var ack = response.read<ackLeaveTab> ();
                            GameConfig.global.enterCode = null;
                            GameConfig.global.currentRoomId = -1;

                            completion (ack.result);
                            responseCompletion ();
                        };

                        connection.sendRequest (request);
                    }
                }
            });
        }

        public void disbandGame (Action<int> completion, string enterCode = null, long roomid = -1) {

            Game.destroyGame (() => {
                var connection = ConnectionManager.sharedInstance.connectionNamed (Config.CONNECTION_NAME_MATCHMAKER);
                if (connection == null) {
                    completion (1);
                } else {
                    var data = new reqDisbandTab ();
                    if (enterCode == null) {
                        enterCode = GameConfig.global.enterCode;
                    }

                    data.enterCode = Encoding.UTF8.GetBytes (enterCode);
                    data.userName = Encoding.Default.GetBytes (Account.localPlayer.userId);
                    if (roomid == -1) {
                        roomid = GameConfig.global.currentRoomId;
                    }
                    data.roomID = roomid;

                    var request = new Request<OurgameHeader> ((int) MatchMakerMessageID.DisbandTab);
                    request.setData (data);
                    request.responseMessageId = (uint) MessageCategory.Ack | (uint) MatchMakerMessageID.DisbandTab;
                    request.responseCallback = (response, responseCompletion) => {
                        var ack = response.read<ackDisbandTab> ();
                        if (ack.result == 0) {
                            this.currentGame = new Game ();
                        }
                        if (ack.currencyType == 1) {
                            Account.localPlayer.updateUserCharge (Account.localPlayer.coin + ack.charge);
                        }
                        GameConfig.global.enterCode = null;
                        GameConfig.global.currentRoomId = -1;

                        completion (ack.result);
                        responseCompletion ();

                    };

                    connection.sendRequest (request);
                }
            });
        }

        public void queryGameRecord (Action<IList<GameRecord>> completion) {
            var connection = ConnectionManager.sharedInstance.connectionNamed (Config.CONNECTION_NAME_MATCHMAKER);
            if (connection == null) {
                completion (null);
            } else {
                var data = new reqGameRecord ();
                data.userName = Encoding.Default.GetBytes (Account.localPlayer.userId);
                data.gameID = 0;

                var request = new Request<OurgameHeader> ((int) MatchMakerMessageID.GameRecord);
                request.setData (data);

                request.responseMessageId = (uint) MessageCategory.Ack | (uint) MatchMakerMessageID.GameRecord;
                request.responseCallback = (response, responseCompletion) => {
                    var ack = response.read<ackGameRecord> ();
                    if (ack != null) {
                        completion (ack.record);
                    }
                    responseCompletion ();
                };

                connection.sendRequest (request);
            }
        }

        public void beginGameAgain (Action<CreateOrJoinGameErrorCode, Action> completion) {
            var connection = ConnectionManager.sharedInstance.connectionNamed (Config.CONNECTION_NAME_MATCHMAKER);
            if (connection == null) {
                completion (CreateOrJoinGameErrorCode.CannotConnectToServer, () => {
                });
                return;
            }

            createGame (GameConfig.global.gameRuleData.numberLap, GameConfig.global.currentGameRule, (errorCode, createGameCompletion) => {
                if (errorCode == 0) {
                    var data = new reqInvite ();
                    data.enterCode = Encoding.Default.GetBytes (GameConfig.global.enterCode);
                    data.userName = Encoding.Default.GetBytes (Account.localPlayer.userId);
                    data.roleName = Encoding.Default.GetBytes (Account.localPlayer.roleName);
                    data.showName = Encoding.Default.GetBytes (Account.localPlayer.nickName);
                    data.avatarUrl = Encoding.Default.GetBytes (Account.localPlayer.avatarUrl);

                    foreach (var item in GameConfig.global.playersInGame) {
                        data.users.Add (new reqInvite.PlayerName () {
                            roleName = Encoding.UTF8.GetBytes (item.Key),
                            showName = Encoding.UTF8.GetBytes (item.Value)
                        });
                    }

                    var request = new Request<OurgameHeader> ((uint) MatchMakerMessageID.Invite);
                    request.setData (data);
                    connection.sendRequest (request);
                }

                Debug.Log (errorCode);
                completion (errorCode, createGameCompletion);
            });
        }

        public void ready (Action completion = null) {
            var connection = ConnectionManager.sharedInstance.connectionNamed (Config.CONNECTION_NAME_MATCHMAKER);
            if (connection == null) {
                return;
            }
            var data = new reqReady ();
            data.userName = Encoding.Default.GetBytes (Account.localPlayer.userId);
            data.roomId = GameConfig.global.currentRoomId;
            data.enterCode = Encoding.Default.GetBytes (GameConfig.global.enterCode);

            var request = new Request<OurgameHeader> ((uint) MatchMakerMessageID.Ready);
            request.setData (data);

            connection.sendRequest (request);
            if (completion != null) {
                completion ();
            }
        }

        public void getCreateRoomList (Action<List<Ourgame.Protocols.Mahjong.RoomInfo>> completion) {
            var connection = ConnectionManager.sharedInstance.connectionNamed (Config.CONNECTION_NAME_MATCHMAKER);
            if (connection == null) {
                return;
            }
            var data = new reqUserRoomList ();
            data.userName = Encoding.Default.GetBytes (Account.localPlayer.userId);
            CommonMessageDialog.show ((dialog) => {
                var request = new Request<OurgameHeader> ((int) MatchMakerMessageID.UseRoomList);
                request.setData (data);
                request.responseMessageId = (uint) MessageCategory.Ack | (uint) MatchMakerMessageID.UseRoomList;
                request.responseCallback = (response, responseCompletion) => {
                    var ack = response.read<ackUserRoomList> ();
                    CommonMessageDialog.dismiss ();
                    completion (ack.roomList);
                    responseCompletion ();
                };

                connection.sendRequest (request);
            }, true);
        }

        void onConnectionBroken (ConnectionEventArgs e, Action completion) {
            if (e.connectionName == Config.CONNECTION_NAME_MATCHMAKER) {
                serverDisconnected.fire (EventArgs.Empty, completion);
            } else {
                completion ();
            }
        }

        void onPlayerInGameChanged (Response<OurgameHeader> response, Action completion) {
            var message = response.read<NtcTabUser> ();
            if (Game.current != null) {
                Game.current.resetPlayers (message.users);
            }

            Dispatcher.main.dispatchAsync (() => {
                playersInGameChanged.fire (EventArgs.Empty, () => {
                    completion ();
                });
            }, 1);
        }

        void onStartGame (Response<OurgameHeader> response, Action completion) {
            var message = response.read<NtcStartGame> ();
            if (Game.current != null) {
                Game.current.gameServerAddress = Encoding.Default.GetString (message.gameIP);
                Game.current.gameServerPort = message.gamePort;
            }
            gameWillBegin.fire (EventArgs.Empty, completion);
        }

        public void startGame (Action<bool> completion) {
            Game.current.begin ((isSuccessful, beginGameCompletion) => {
                if (isSuccessful) {
                    gameBegin.fire (EventArgs.Empty, () => {
                        _current.Dispose ();
                        _current = null;
                        beginGameCompletion ();
                    });
                } else {
                    beginGameFailed.fire (new BeginGameFailedEventArgs (5), beginGameCompletion);
                }
                completion (isSuccessful);
            });
        }

        void onGameListUpdated (Response<OurgameHeader> response, Action completion) {
            _chargConfigs.Clear ();
            var ack = response.read<NtcGameList> ();

            _gameConfigId = ack.games [0].gameCfgId;
            _chargConfigs.AddRange (ack.games [0].charges);

            serverReady.fire (EventArgs.Empty, completion);
        }

        void onMessage (Response<OurgameHeader> response, Action completion) {
            var ack = response.read<NtcMsg> ();

            _messageList.AddRange (from msg in ack.msgs
                                   where msg.type == 0
                                   orderby msg.priority descending
                                   select new KeyValuePair<int, string> (msg.interval, Encoding.UTF8.GetString (msg.contents)));

            int count = ack.msgs.Count ((arg) => arg.type == 1);
            if (count > 0) {
                announcementArrived.fire (new AnnouncementArrivedEventArgs (count), completion);
            } else {
                newMessageArrived.fire (EventArgs.Empty, completion);
            }
        }

        void onInvitationArrived (Response<OurgameHeader> response, Action completion) {
            var ack = response.read<NtcInvite> ();
            invitationArrived.fire (new InvitationArrivedEventArgs (ack), completion);
        }

        void onDisbandTab (Response<OurgameHeader> response, Action completion) {
            var ack = response.read<NtcDisbandTab> ();
            if (0 == Game.current.gameOwnerSeat && ack.currencyType == 1) {
                Account.localPlayer.updateUserCharge (Account.localPlayer.coin + ack.charge);
            }

            gameDisbanded.fire (new GameDisbandedEventArgs (Encoding.UTF8.GetString (ack.houseOwner)), () => {
                GameConfig.global.currentRoomId = -1;
                GameConfig.global.enterCode = null;
                completion ();
            });
        }
    }
}

