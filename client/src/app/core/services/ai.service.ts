import { HttpClient } from '@angular/common/http';
import { Injectable } from '@angular/core';
import { environment } from 'src/environments/environment';
import { IAiChatRequest, IAiChatResponse } from 'src/app/shared/models/aiChat';

@Injectable({
  providedIn: 'root'
})
export class AiService {
  baseUrl = environment.apiUrl;

  constructor(private http: HttpClient) { }

  askQuestion(question: string) {
    const payload: IAiChatRequest = {question};
    return this.http.post<IAiChatResponse>(this.baseUrl + 'ai/chat', payload);
  }
}
