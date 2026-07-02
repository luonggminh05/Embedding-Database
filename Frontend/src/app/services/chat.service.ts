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
  private get hubUrl(): string {
    return typeof window !== 'undefined'
      ? `${window.location.protocol}//${window.location.hostname}:30001/chathub`
      : 'http://YOUR_VM_IP:30001/chathub';
  }

  public messages = signal<ChatMessage[]>([]);
  public isResponding = signal<boolean>(false);
  public currentStreamedMessage = signal<string>('');
  
  // LÆ°u táº¡m citations cá»§a luá»“ng hiá»‡n táº¡i
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
        console.error('Lá»—i khi gá»­i tin nháº¯n qua WebSocket:', err);
        this.isResponding.set(false);
      });
    } else {
      console.error('SignalR chÆ°a káº¿t ná»‘i!');
      this.isResponding.set(false);
    }
  }

  private addMessage(msg: ChatMessage) {
    this.messages.update(msgs => [...msgs, msg]);
  }
}
