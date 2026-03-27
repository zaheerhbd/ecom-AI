export interface IAiChatRequest {
  question: string;
}

export interface IAiChatSource {
  id: string;
  sourceType: string;
  title: string;
  text: string;
  metadata: {[key: string]: string};
}

export interface IAiChatResponse {
  answer: string;
  sources: IAiChatSource[];
  followUpSuggestions: string[];
}
