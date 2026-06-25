import { Component, OnInit, ElementRef, ViewChild, effect } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { HttpClient } from '@angular/common/http';
import { ChatService, ChatMessage } from './services/chat.service';

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
    // Sử dụng effect để tự động scroll khi messages hoặc streamed message thay đổi
    effect(() => {
      // Đọc các giá trị signal để effect theo dõi
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

      this.chatService.messages.update(msgs => [...msgs, { role: 'bot', content: `Đang tải lên file: ${file.name}...` }]);
      this.scrollToBottom();

      this.http.post('http://localhost:30001/api/document/upload', formData).subscribe({
        next: (res: any) => {
          this.isUploading = false;
          this.chatService.messages.update(msgs => [...msgs, { role: 'bot', content: `Đã nạp file ${file.name} thành công. Hệ thống đang tiến hành xử lý...` }]);
          this.scrollToBottom();
        },
        error: (err) => {
          this.isUploading = false;
          this.chatService.messages.update(msgs => [...msgs, { role: 'bot', content: `Lỗi tải file: ${err.message}` }]);
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
