import { Component } from '@angular/core';
import { AiService } from '../services/ai.service';
import { IAiChatResponse } from 'src/app/shared/models/aiChat';

@Component({
  selector: 'app-ai-playground',
  templateUrl: './ai-playground.component.html',
  styleUrls: ['./ai-playground.component.scss']
})
export class AiPlaygroundComponent {
  question = 'show me nike products';
  loading = false;
  result: IAiChatResponse | null = null;
  error: string | null = null;

  samplePrompts = [
    'show me boots',
    'find affordable products under 100',
    'what delivery options do you have?'
  ];

  constructor(private aiService: AiService) { }

  askQuestion() {
    if (!this.question.trim()) {
      this.error = 'Please enter a question.';
      return;
    }

    this.loading = true;
    this.error = null;

    this.aiService.askQuestion(this.question).subscribe({
      next: (response) => {
        this.result = response;
        this.loading = false;
      },
      error: () => {
        this.error = 'The AI-Lab endpoint could not be reached. Make sure the API is running.';
        this.loading = false;
      }
    });
  }

  usePrompt(prompt: string) {
    this.question = prompt;
    this.askQuestion();
  }
}
