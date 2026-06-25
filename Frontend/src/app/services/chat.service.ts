import { Injectable, signal, WritableSignal } from '@angular/core';
import * as signalR from '@microsoft/signalr';

export interface ChatMessage {
  role: 'user' | 'bot';
  content: string;
  citations?: any[];
}

@Injectable({
  providedIn: 'root'
})
export class ChatService {
  private hubConnection: signalR.HubConnection | undefined;
  private readonly hubUrl = 'http://localhost:30001/chathub';

  public messages = signal<ChatMessage[]>([]);
  public isResponding = signal<boolean>(false);
  public currentStreamedMessage = signal<string>('');
  
  // Lưu tạm citations của luồng hiện tại
  private currentCitations: any[] = [];

  constructor() {}

  public startConnection() {
    this.hubConnection = new signalR.HubConnectionBuilder()
      .withUrl(this.hubUrl)
      .withAutomaticReconnect()
      .build();

    this.hubConnection.start()
      .then(() => console.log('SignalR Connected!'))
      .catch(err => console.log('Error while starting connection: ' + err));

    this.hubConnection.onreconnecting(error => {
      console.log(`Connection lost due to error "${error}". Reconnecting.`);
    });

    this.hubConnection.on('ChatStarted', () => {
      this.isResponding.set(true);
      this.currentStreamedMessage.set('');
    });

    this.hubConnection.on('ReceiveToken', (token: string) => {
      this.currentStreamedMessage.update(current => current + token);
    });

    this.hubConnection.on('ReceiveCitations', (citations: any[]) => {
      this.currentCitations = citations;
    });

    this.hubConnection.on('ChatEnded', () => {
      this.isResponding.set(false);
      const fullMessage = this.currentStreamedMessage();
      if (fullMessage) {
        this.addMessage({ 
          role: 'bot', 
          content: fullMessage,
          citations: this.currentCitations.length > 0 ? [...this.currentCitations] : undefined
        });
        this.currentStreamedMessage.set('');
        this.currentCitations = [];
      }
    });
  }

  public sendMessage(message: string) {
    this.addMessage({ role: 'user', content: message });
    this.isResponding.set(true);
    this.currentCitations = [];
    
    if (this.hubConnection && this.hubConnection.state === signalR.HubConnectionState.Connected) {
      this.hubConnection.invoke('Ask', message).catch(err => {
        console.error('Lỗi khi gửi tin nhắn qua WebSocket:', err);
        this.isResponding.set(false);
      });
    } else {
      console.error('SignalR chưa kết nối!');
      this.isResponding.set(false);
    }
  }

  private addMessage(msg: ChatMessage) {
    this.messages.update(msgs => [...msgs, msg]);
  }
}
