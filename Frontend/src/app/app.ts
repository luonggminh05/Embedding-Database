import { Component, OnInit, ElementRef, ViewChild, effect } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { HttpClient } from '@angular/common/http';
import { ChatService, ChatMessage } from './services/chat.service';
import { API_BASE_URL } from './app.constants';

@Component({
  selector: 'app-root',
  standalone: true,
  imports: [CommonModule, FormsModule],
  templateUrl: './app.html',
  styleUrls: ['./app.css']
})
export class AppComponent implements OnInit {
  public userInput: string = '';
  public isUploading: boolean = false;

  @ViewChild('chatContainer') private chatContainer!: ElementRef;

  constructor(public chatService: ChatService, private http: HttpClient) {
    // Sá»­ dá»¥ng effect Ä‘á»ƒ tá»± Ä‘á»™ng scroll khi messages hoáº·c streamed message thay Ä‘á»•i
    effect(() => {
      // Äá»c cÃ¡c giÃ¡ trá»‹ signal Ä‘á»ƒ effect theo dÃµi
      this.chatService.messages();
      this.chatService.currentStreamedMessage();
      this.scrollToBottom();
    });
  }

  ngOnInit() {
    this.chatService.startConnection();
  }

  public sendMessage() {
    if (!this.userInput.trim() || this.chatService.isResponding()) return;
    
    this.chatService.sendMessage(this.userInput);
    this.userInput = '';
  }

  public onFileSelected(event: any) {
    const file: File = event.target.files[0];
    if (file) {
      this.isUploading = true;
      const formData = new FormData();
      formData.append('file', file);

      this.chatService.messages.update(msgs => [...msgs, { role: 'bot', content: `Äang táº£i lÃªn file: ${file.name}...` }]);
      this.scrollToBottom();

      this.http.post(`${API_BASE_URL}/api/document/upload`, formData).subscribe({
        next: (res: any) => {
          this.isUploading = false;
          this.chatService.messages.update(msgs => [...msgs, { role: 'bot', content: `ÄÃ£ náº¡p file ${file.name} thÃ nh cÃ´ng. Há»‡ thá»‘ng Ä‘ang tiáº¿n hÃ nh xá»­ lÃ½...` }]);
          this.scrollToBottom();
        },
        error: (err) => {
          this.isUploading = false;
          this.chatService.messages.update(msgs => [...msgs, { role: 'bot', content: `Lá»—i táº£i file: ${err.message}` }]);
          this.scrollToBottom();
        }
      });
    }
  }

  private scrollToBottom(): void {
    setTimeout(() => {
      if (this.chatContainer) {
        this.chatContainer.nativeElement.scrollTop = this.chatContainer.nativeElement.scrollHeight;
      }
    }, 50);
  }
}
